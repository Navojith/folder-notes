using FolderNotes.App.Explorer;
using FolderNotes.App.Explorer.Com;
using FolderNotes.App.Explorer.Native;

namespace FolderNotes.App.Tests.Explorer;

/// <summary>
/// A scriptable Explorer for the engine and tracker tests: frames and tabs, the foreground, the shell window and its
/// process, injected COM failures, and handle accounting. It is both the gateway factory (COM side) and the window
/// queries (<see cref="IWin32Shell"/>), so they always agree. Thread-safe (tracker tests use real worker threads).
/// </summary>
internal sealed class FakeExplorerDesktop : IShellWindowsGatewayFactory, IWin32Shell
{
    public sealed record Tab(nint Frame, nint Key, string? Path, bool Visible);

    private readonly Lock _gate = new();
    private readonly List<Tab> _tabs = [];
    private readonly HashSet<nint> _hiddenFromRoot = [];
    private readonly Dictionary<nint, uint> _framePid = [];
    private readonly Dictionary<nint, nint> _owner = [];
    private readonly HashSet<nint> _minimized = [];
    private readonly HashSet<nint> _nonExplorerWindows = [];
    private readonly Queue<int> _readFailures = new();
    private int _liveHandles;
    private int _gatewaysCreated;
    private int _liveGateways;
    private int _enumerations;
    private int _reads;

    public nint Foreground { get; set; }

    public nint ShellWindow { get; set; } = 1000;

    public uint ShellPid { get; set; } = 500;

    /// <summary>HRESULT the root's enumeration fails with while non-zero (a dead root).</summary>
    public int EnumerateHr { get; set; }

    /// <summary>The next <c>TryCreate</c> calls fail with these HRESULTs, in order, then succeed.</summary>
    public Queue<int> CreateFailures { get; } = [];

    /// <summary>A private empty server: the root enumerates nothing although frames exist (04 F, L7).</summary>
    public bool RootIsEmpty { get; set; }

    /// <summary>When set, <c>TryReadPath</c> waits on it (a hung Explorer). Released by the test.</summary>
    public ManualResetEventSlim? BlockReads { get; set; }

    /// <summary>The next N reads block on <see cref="BlockReads"/>.</summary>
    public int BlockNextReads;

    /// <summary>The next read throws an exception (a bug in a gateway must not escape).</summary>
    public bool ThrowOnNextRead { get; set; }

    /// <summary>What a read that was blocked (by <see cref="BlockReads"/>) returns once released, so only that stuck worker can ever publish it.</summary>
    public string? BlockedReadResultPath { get; set; }

    /// <summary>Blocked reads that were released and ran to completion.</summary>
    public int BlockedReadsCompleted;

    public int LiveHandles => Volatile.Read(ref _liveHandles);

    public int GatewaysCreated => Volatile.Read(ref _gatewaysCreated);

    public int LiveGateways => Volatile.Read(ref _liveGateways);

    public int Enumerations => Volatile.Read(ref _enumerations);

    public int Reads => Volatile.Read(ref _reads);

    public int CreateAttemptsWhileShellDown { get; private set; }

    public void AddTab(nint frame, nint key, string? path, bool visible = true, uint pid = 500)
    {
        lock (_gate)
        {
            _tabs.Add(new Tab(frame, key, path, visible));
            _framePid[frame] = pid;
        }
    }

    public void Navigate(nint frame, string? path)
    {
        lock (_gate)
        {
            int i = _tabs.FindIndex(t => t.Frame == frame && t.Visible);
            _tabs[i] = _tabs[i] with { Path = path };
        }
    }

    public void SwitchTab(nint frame, nint key)
    {
        lock (_gate)
        {
            for (int i = 0; i < _tabs.Count; i++)
            {
                if (_tabs[i].Frame == frame)
                {
                    _tabs[i] = _tabs[i] with { Visible = _tabs[i].Key == key };
                }
            }
        }
    }

    public void CloseTab(nint key)
    {
        lock (_gate)
        {
            _tabs.RemoveAll(t => t.Key == key);
        }
    }

    /// <summary>The tab exists as a window but the root does not enumerate it (an elevated Explorer, 04 G L8).</summary>
    public void HideFromRoot(nint key)
    {
        lock (_gate)
        {
            _hiddenFromRoot.Add(key);
        }
    }

    public void SetOwner(nint window, nint owner)
    {
        lock (_gate)
        {
            _owner[window] = owner;
        }
    }

    public void AddForeignWindow(nint window)
    {
        lock (_gate)
        {
            _nonExplorerWindows.Add(window);
        }
    }

    public void Minimize(nint frame)
    {
        lock (_gate)
        {
            _minimized.Add(frame);
        }
    }

    public void FailNextReads(params int[] hresults)
    {
        lock (_gate)
        {
            foreach (var hr in hresults)
            {
                _readFailures.Enqueue(hr);
            }
        }
    }

    // ---- IShellWindowsGatewayFactory -------------------------------------------------------------------

    public int TryCreate(out IShellWindowsGateway? gateway)
    {
        gateway = null;
        lock (_gate)
        {
            if (ShellWindow == 0)
            {
                CreateAttemptsWhileShellDown++;
            }

            if (CreateFailures.TryDequeue(out int hr))
            {
                return hr;
            }

            Interlocked.Increment(ref _gatewaysCreated);
            Interlocked.Increment(ref _liveGateways);
            gateway = new Gateway(this);
            return 0;
        }
    }

    private sealed class Handle(FakeExplorerDesktop owner, nint key) : TabHandle
    {
        private int _disposed;

        public nint Key { get; } = key;

        public override void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Interlocked.Decrement(ref owner._liveHandles);
            }
        }
    }

    private sealed class Gateway(FakeExplorerDesktop owner) : IShellWindowsGateway
    {
        private int _disposed;

        public int TryEnumerateTabs(List<DiscoveredTab<TabHandle>> into)
        {
            Interlocked.Increment(ref owner._enumerations);
            lock (owner._gate)
            {
                if (owner.EnumerateHr < 0)
                {
                    return owner.EnumerateHr;
                }

                if (owner.RootIsEmpty)
                {
                    return 0;
                }

                foreach (var t in owner._tabs.Where(t => !owner._hiddenFromRoot.Contains(t.Key)))
                {
                    Interlocked.Increment(ref owner._liveHandles);
                    into.Add(new DiscoveredTab<TabHandle>(t.Frame, t.Key, new Handle(owner, t.Key)));
                }

                return 0;
            }
        }

        public PathReadResult TryReadPath(TabHandle tab)
        {
            Interlocked.Increment(ref owner._reads);
            bool wasBlocked = false;
            if (Interlocked.Decrement(ref owner.BlockNextReads) >= 0)
            {
                owner.BlockReads?.Wait();
                wasBlocked = true;
                Interlocked.Increment(ref owner.BlockedReadsCompleted);
            }

            lock (owner._gate)
            {
                if (owner.ThrowOnNextRead)
                {
                    owner.ThrowOnNextRead = false;
                    throw new InvalidOperationException("gateway bug");
                }

                if (owner._readFailures.TryDequeue(out int hr) && hr != 0)
                {
                    return PathReadResult.Failure(hr);
                }

                if (wasBlocked && owner.BlockedReadResultPath is { } zombiePath)
                {
                    return PathReadResult.Success(zombiePath);
                }

                var key = ((Handle)tab).Key;
                var t = owner._tabs.FirstOrDefault(x => x.Key == key);
                return t is null ? PathReadResult.Failure(unchecked((int)0x80004005)) : PathReadResult.Success(t.Path);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Interlocked.Decrement(ref owner._liveGateways);
            }
        }
    }

    // ---- IWin32Shell -----------------------------------------------------------------------------------

    public nint GetForegroundWindow() => Foreground;

    public bool IsExplorerFrame(nint hwnd)
    {
        lock (_gate)
        {
            return _framePid.ContainsKey(hwnd);
        }
    }

    public nint OwnerOf(nint hwnd)
    {
        lock (_gate)
        {
            return _owner.GetValueOrDefault(hwnd);
        }
    }

    public bool IsMinimized(nint hwnd)
    {
        lock (_gate)
        {
            return _minimized.Contains(hwnd);
        }
    }

    public nint GetShellWindow() => ShellWindow;

    public uint ProcessIdOf(nint hwnd)
    {
        if (hwnd == ShellWindow)
        {
            return ShellPid;
        }

        lock (_gate)
        {
            return _framePid.GetValueOrDefault(hwnd);
        }
    }

    public bool IsWindow(nint hwnd)
    {
        lock (_gate)
        {
            return _tabs.Any(t => t.Key == hwnd || t.Frame == hwnd) || _nonExplorerWindows.Contains(hwnd) || hwnd == ShellWindow;
        }
    }

    public IReadOnlyList<ChildTab> TabChildren(nint frame)
    {
        lock (_gate)
        {
            // a tab whose key equals its frame is the implicit single tab: no ShellTabWindowClass children (04 C)
            return _tabs.Where(t => t.Frame == frame && t.Key != frame).Select(t => new ChildTab(t.Key, t.Visible)).ToList();
        }
    }

    public bool AnyExplorerFrameExists()
    {
        lock (_gate)
        {
            return _tabs.Count > 0;
        }
    }
}
