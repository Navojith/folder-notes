namespace FolderNotes.App.Explorer;

/// <summary>A tab found by enumerating <c>IShellWindows</c>; the map takes ownership of <see cref="Handle"/>.</summary>
internal readonly record struct DiscoveredTab<THandle>(nint FrameHwnd, nint TabKey, THandle Handle)
    where THandle : class, IDisposable;

internal sealed class TabEntry<THandle>(nint tabKey, nint frameHwnd, THandle handle)
    where THandle : class, IDisposable
{
    public nint TabKey { get; } = tabKey;

    public nint FrameHwnd { get; internal set; } = frameHwnd;

    public THandle Handle { get; internal set; } = handle;

    public string? LastPath { get; internal set; }

    public int ConsecutiveOtherFailures { get; internal set; }

    internal long LastUsed { get; set; }
}

internal enum FailureEffect
{
    Unknown,
    Kept,
    Dropped,
}

internal readonly record struct ReconcileResult(int Added, int Replaced, int Removed, int Evicted);

/// <summary>
/// The browser→tab cache (04 § ExplorerTracker design C), keyed by the tab's <c>ShellTabWindowClass</c> HWND.
/// Pure: the handle type is anything disposable, so tests use a counting fake and production uses a COM
/// reference holder. The map owns every handle it is given and disposes each exactly once. Single-threaded
/// (the tracker's STA thread).
/// </summary>
internal sealed class TabMap<THandle> : IDisposable
    where THandle : class, IDisposable
{
    public const int DefaultCapacity = 256;
    public const int MaxConsecutiveOtherFailures = 3;

    private readonly Dictionary<nint, TabEntry<THandle>> _entries = [];
    private readonly int _capacity;
    private long _clock;

    public TabMap(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
    }

    public int Count => _entries.Count;

    public bool Contains(nint tabKey) => _entries.ContainsKey(tabKey);

    /// <summary>Looks up an entry and marks it recently used.</summary>
    public bool TryGet(nint tabKey, out TabEntry<THandle> entry)
    {
        if (_entries.TryGetValue(tabKey, out entry!))
        {
            entry.LastUsed = ++_clock;
            return true;
        }

        return false;
    }

    public IReadOnlyList<nint> TabsOf(nint frameHwnd) =>
        [.. _entries.Values.Where(e => e.FrameHwnd == frameHwnd).Select(e => e.TabKey)];

    /// <summary>
    /// Applies a full enumeration (a "rebuild"): adds new tabs, replaces the handle of known ones (the fresh
    /// handle is the proven-live one; the old is released), removes tabs that are no longer listed, then enforces
    /// the capacity by evicting the least recently used entries outside <paramref name="foregroundFrame"/>.
    /// </summary>
    public ReconcileResult Reconcile(IReadOnlyList<DiscoveredTab<THandle>> discovered, nint foregroundFrame)
    {
        ArgumentNullException.ThrowIfNull(discovered);
        HashSet<nint> seen = [];
        int added = 0, replaced = 0;

        foreach (var d in discovered)
        {
            if (!seen.Add(d.TabKey))
            {
                d.Handle.Dispose(); // the same tab listed twice: the first one wins
                continue;
            }

            if (_entries.TryGetValue(d.TabKey, out var existing))
            {
                existing.Handle.Dispose();
                existing.Handle = d.Handle;
                existing.FrameHwnd = d.FrameHwnd;
                existing.ConsecutiveOtherFailures = 0;
                replaced++; // a rebuild is not a use: recency stays as the last real lookup left it
            }
            else
            {
                _entries[d.TabKey] = new TabEntry<THandle>(d.TabKey, d.FrameHwnd, d.Handle) { LastUsed = ++_clock };
                added++;
            }
        }

        int removed = 0;
        foreach (var key in _entries.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            Remove(key);
            removed++;
        }

        return new ReconcileResult(added, replaced, removed, Evict(foregroundFrame));
    }

    public bool Remove(nint tabKey)
    {
        if (!_entries.Remove(tabKey, out var e))
        {
            return false;
        }

        e.Handle.Dispose();
        return true;
    }

    /// <summary>Drops every tab of a frame (the frame's <c>DESTROY</c> event). Returns how many.</summary>
    public int RemoveFrame(nint frameHwnd)
    {
        int n = 0;
        foreach (var key in TabsOf(frameHwnd))
        {
            n += Remove(key) ? 1 : 0;
        }

        return n;
    }

    /// <summary>Drops entries whose window no longer exists (a missed <c>DESTROY</c>). Returns how many.</summary>
    public int SweepDead(Func<nint, bool> isWindow)
    {
        ArgumentNullException.ThrowIfNull(isWindow);
        int n = 0;
        foreach (var key in _entries.Keys.Where(k => !isWindow(k)).ToList())
        {
            n += Remove(key) ? 1 : 0;
        }

        return n;
    }

    public void RecordSuccess(nint tabKey, string? path)
    {
        if (_entries.TryGetValue(tabKey, out var e))
        {
            e.LastPath = path;
            e.ConsecutiveOtherFailures = 0;
        }
    }

    /// <summary>Applies the B.5 action table to one entry: Transient keeps, Gone drops, Other drops on the 3rd in a row.</summary>
    public FailureEffect RecordFailure(nint tabKey, ComFailureClass failure)
    {
        if (!_entries.TryGetValue(tabKey, out var e))
        {
            return FailureEffect.Unknown;
        }

        switch (failure)
        {
            case ComFailureClass.Gone:
                Remove(tabKey);
                return FailureEffect.Dropped;
            case ComFailureClass.Other:
                if (++e.ConsecutiveOtherFailures >= MaxConsecutiveOtherFailures)
                {
                    Remove(tabKey);
                    return FailureEffect.Dropped;
                }

                return FailureEffect.Kept;
            default:
                return FailureEffect.Kept; // Transient (and None): the entry stays
        }
    }

    public void Clear()
    {
        foreach (var e in _entries.Values)
        {
            e.Handle.Dispose();
        }

        _entries.Clear();
    }

    public void Dispose() => Clear();

    private int Evict(nint foregroundFrame)
    {
        int evicted = 0;
        while (_entries.Count > _capacity)
        {
            TabEntry<THandle>? victim = null;
            foreach (var e in _entries.Values)
            {
                if (e.FrameHwnd != foregroundFrame && (victim is null || e.LastUsed < victim.LastUsed))
                {
                    victim = e;
                }
            }

            if (victim is null)
            {
                break; // everything left belongs to the foreground frame; never evict what the user is looking at
            }

            Remove(victim.TabKey);
            evicted++;
        }

        return evicted;
    }
}

internal readonly record struct ChildTab(nint Hwnd, bool Visible);

internal enum ActiveTabKind
{
    /// <summary>The first visible <c>ShellTabWindowClass</c> child.</summary>
    Tab,

    /// <summary>No tab windows at all (Windows 10, or a frame still being built): the frame is its own tab.</summary>
    ImplicitFrame,

    /// <summary>Tab windows exist but none is visible (a transient state): unresolved.</summary>
    Unresolved,
}

internal readonly record struct ActiveTab(ActiveTabKind Kind, nint Key);

/// <summary>04 § C: the active tab is the visible tab window; z-order only breaks a tie between visible ones.</summary>
internal static class ActiveTabResolver
{
    public static ActiveTab Resolve(nint frameHwnd, IReadOnlyList<ChildTab> tabChildrenInZOrder)
    {
        ArgumentNullException.ThrowIfNull(tabChildrenInZOrder);
        if (tabChildrenInZOrder.Count == 0)
        {
            return new ActiveTab(ActiveTabKind.ImplicitFrame, frameHwnd);
        }

        foreach (var c in tabChildrenInZOrder)
        {
            if (c.Visible)
            {
                return new ActiveTab(ActiveTabKind.Tab, c.Hwnd);
            }
        }

        return new ActiveTab(ActiveTabKind.Unresolved, 0);
    }
}

/// <summary>04 D.7: which Explorer frame, if any, the foreground window stands for.</summary>
internal static class ForegroundResolver
{
    private const int MaxOwnerDepth = 8;

    /// <summary>
    /// The frame when <paramref name="foreground"/> is an Explorer frame or is owned by one (our docked panel),
    /// and the frame is not minimized; otherwise 0 (Inactive).
    /// </summary>
    public static nint Resolve(nint foreground, Func<nint, bool> isExplorerFrame, Func<nint, nint> ownerOf, Func<nint, bool> isMinimized)
    {
        ArgumentNullException.ThrowIfNull(isExplorerFrame);
        ArgumentNullException.ThrowIfNull(ownerOf);
        ArgumentNullException.ThrowIfNull(isMinimized);
        var h = foreground;
        for (int depth = 0; h != 0 && depth < MaxOwnerDepth; depth++)
        {
            if (isExplorerFrame(h))
            {
                return isMinimized(h) ? 0 : h;
            }

            h = ownerOf(h);
        }

        return 0;
    }
}
