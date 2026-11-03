using System.Collections.Concurrent;

namespace FolderNotes.App.Explorer.Native;

internal enum HookStartResult
{
    /// <summary>The thread is running and the always-on hook is installed.</summary>
    Started,

    /// <summary>A hook thread is already running for this object.</summary>
    AlreadyStarted,

    /// <summary>The process thread budget refused a hook thread (a live one exists, or one was abandoned earlier).</summary>
    BudgetRefused,

    /// <summary><c>SetWinEventHook</c> failed or the thread died while starting: run on the poll (04 J, H1).</summary>
    InstallFailed,

    /// <summary>The thread did not answer in time and was abandoned; never replaced in this process (04 J, H2).</summary>
    TimedOut,
}

internal sealed record HookThreadOptions
{
    public static HookThreadOptions Default { get; } = new();

    /// <summary>How long <see cref="HookThread.Start"/> waits for the thread to install its hook (04 B.7).</summary>
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>How long <see cref="HookThread.Stop"/> waits for uninstall + exit before abandoning the thread (04 B.7: 1 s).</summary>
    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(1);
}

/// <summary>Window-event ids this tracker listens to, and what each means as a <see cref="TrackerSignal"/> (04 D.1).</summary>
internal static class WinEventMap
{
    public const uint SystemForeground = 0x0003;
    public const uint SystemMinimizeStart = 0x0016;
    public const uint SystemMinimizeEnd = 0x0017;
    public const uint ObjectCreate = 0x8000;
    public const uint ObjectDestroy = 0x8001;
    public const uint ObjectShow = 0x8002;
    public const uint ObjectHide = 0x8003;
    public const uint ObjectNameChange = 0x800C;

    /// <summary>The ranges installed while a scope (an Explorer process) is set. Each range maps to exactly one signal.</summary>
    public static readonly (uint Min, uint Max)[] ScopedRanges =
    [
        (ObjectCreate, ObjectDestroy),
        (ObjectShow, ObjectHide),
        (ObjectNameChange, ObjectNameChange),
        (SystemMinimizeStart, SystemMinimizeEnd),
    ];

    public static TrackerSignal ToSignal(uint eventId) => eventId switch
    {
        SystemForeground => TrackerSignal.Foreground,
        SystemMinimizeStart or SystemMinimizeEnd => TrackerSignal.Minimize,
        ObjectCreate or ObjectDestroy => TrackerSignal.CreateDestroy,
        ObjectShow or ObjectHide => TrackerSignal.ShowHide,
        ObjectNameChange => TrackerSignal.NameChange,
        _ => TrackerSignal.None,
    };
}

/// <summary>
/// The dedicated hook thread of 04 B.7 (decision Q2). It owns the WinEvent hooks and a plain Win32 message loop,
/// and it <b>never touches COM, the file system or the UI</b>: callbacks map the event to a bit and hand it to the
/// <see cref="SignalRouter"/>. Install, re-scope and uninstall run only on this thread, driven by commands posted from
/// other threads, so hook handles are never shared and <c>UnhookWinEvent</c> is always called on the installing thread.
/// <para>
/// Lifecycle bounds: at most one live hook thread (process budget), one creation per <see cref="Start"/>, a
/// <see cref="Stop"/> that never waits longer than <see cref="HookThreadOptions.StopTimeout"/>, and a thread that
/// had to be abandoned is never replaced in this process.
/// </para>
/// </summary>
internal sealed class HookThread : IDisposable, IHookScope
{
    private const uint WmCommand = MessageLoopNative.WM_APP + 1;

    private enum CommandKind { Install, SetScope, Uninstall, Quit }

    private sealed record Command(CommandKind Kind, uint Pid, long Sequence, TaskCompletionSource<bool>? Ack);

    private sealed class Session
    {
        public Thread Thread { get; set; } = null!;

        public ConcurrentQueue<Command> Commands { get; } = new();

        public ManualResetEventSlim Exited { get; } = new(false);

        public uint ThreadId;
        public volatile bool Retired;
        public int SlotHeld = 1;
        public volatile bool InstallOk;
        public int GlobalHooks;
        public int ScopedHooks;
        public uint ScopePid;
        public long AppliedSequence;
        public nint ForegroundHook;
        public readonly List<nint> Scoped = [];
        public int ManagedThreadId;
    }

    private readonly IWinEventApi _api;
    private readonly SignalRouter _router;
    private readonly TrackerProcessGuard _guard;
    private readonly HookThreadOptions _options;
    private readonly Action<string> _log;
    private readonly Lock _gate = new();
    private readonly WinEventCallback _callback;   // one instance for the whole life: the GC must not move or collect it
    private Session? _session;
    private long _scopeSequence;
    private int _hookThreadsCreated;

    public HookThread(IWinEventApi api, SignalRouter router, TrackerProcessGuard guard, Action<string>? log = null, HookThreadOptions? options = null)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _guard = guard ?? throw new ArgumentNullException(nameof(guard));
        _options = options ?? HookThreadOptions.Default;
        _log = log ?? (_ => { });
        _callback = OnWinEvent;
    }

    /// <summary>True while the hook thread runs and its always-on hook is installed. False = poll-only (04 J, H1).</summary>
    public bool HooksActive
    {
        get { lock (_gate) { return _session is { Retired: false, InstallOk: true } s && !s.Exited.IsSet; } }
    }

    /// <summary>The last hook thread had to be abandoned; no replacement will be created in this process.</summary>
    public bool Abandoned { get; private set; }

    /// <summary>How many hook threads this object created over its life (a bound the tests check).</summary>
    public int ThreadsCreated => Volatile.Read(ref _hookThreadsCreated);

    /// <summary>Hooks currently installed (always-on + scoped); for tests and <c>TrackerStats</c>.</summary>
    public int InstalledHookCount
    {
        get { lock (_gate) { return _session is { } s ? Volatile.Read(ref s.GlobalHooks) + Volatile.Read(ref s.ScopedHooks) : 0; } }
    }

    /// <summary>The process the scoped hooks currently listen to (0 = none).</summary>
    public uint ScopePid
    {
        get { lock (_gate) { return _session is { } s ? Volatile.Read(ref s.ScopePid) : 0; } }
    }

    /// <summary>Managed id of the live hook thread, 0 if none (tests assert calls happen there).</summary>
    public int ManagedThreadId
    {
        get { lock (_gate) { return _session is { } s ? s.ManagedThreadId : 0; } }
    }

    /// <summary>Reserve the thread, start it and install the always-on (<c>EVENT_SYSTEM_FOREGROUND</c>) hook. Bounded by <see cref="HookThreadOptions.StartTimeout"/>.</summary>
    public HookStartResult Start()
    {
        var ack = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new Session();
        lock (_gate)
        {
            if (_session is { Retired: false } live && !live.Exited.IsSet)
            {
                return HookStartResult.AlreadyStarted;
            }

            if (Abandoned || !_guard.TryReserveThread(TrackerThreadKind.Hook))
            {
                return HookStartResult.BudgetRefused;
            }

            session.Commands.Enqueue(new Command(CommandKind.Install, 0, 0, ack));
            session.Thread = new Thread(() => Run(session)) { IsBackground = true, Name = "ExplorerTracker.Hooks" };
            _session = session;
            _hookThreadsCreated++;
        }

        session.Thread.UnsafeStart();
        var installed = ack.Task;
        var real = session;

        bool answered;
        try
        {
            answered = installed.Wait(_options.StartTimeout);
        }
        catch (AggregateException)
        {
            answered = true;
        }

        if (!answered)
        {
            _log("Hook thread did not answer in time; abandoning it (poll-only from now on).");
            AbandonSession(real);
            return HookStartResult.TimedOut;
        }

        if (!installed.IsCompletedSuccessfully || !installed.Result)
        {
            _log("Hook thread could not install its hook; the tracker runs on the poll (poll-only).");
            Stop();
            return HookStartResult.InstallFailed;
        }

        real.InstallOk = true;
        return HookStartResult.Started;
    }

    /// <summary>
    /// Ask the hook thread to listen to Explorer's process <paramref name="processId"/> (0 removes the scoped hooks).
    /// Non-blocking; commands run in order on the hook thread and an older sequence never overrides a newer one.
    /// </summary>
    public bool SetScope(uint processId)
    {
        Session? s;
        lock (_gate)
        {
            s = _session;
            if (s is not { Retired: false } || s.Exited.IsSet)
            {
                return false;
            }
        }

        long seq = Interlocked.Increment(ref _scopeSequence);
        return Post(s, new Command(CommandKind.SetScope, processId, seq, null));
    }

    /// <summary>Same as <see cref="SetScope(uint)"/> with an explicit sequence (used by tests to prove stale commands are ignored).</summary>
    internal bool SetScopeWithSequence(uint processId, long sequence)
    {
        Session? s;
        lock (_gate)
        {
            s = _session;
        }

        return s is not null && Post(s, new Command(CommandKind.SetScope, processId, sequence, null));
    }

    /// <summary>
    /// Remove every hook on the hook thread, end its loop, and wait up to <see cref="HookThreadOptions.StopTimeout"/>.
    /// Returns true when the thread exited in time; false means it was abandoned (and will not be replaced).
    /// Harmless if never started or already stopped.
    /// </summary>
    public bool Stop()
    {
        Session? s;
        lock (_gate)
        {
            s = _session;
            if (s is null)
            {
                return true;
            }
        }

        if (!s.Exited.IsSet)
        {
            var ack = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!Post(s, new Command(CommandKind.Uninstall, 0, 0, ack)) || !Post(s, new Command(CommandKind.Quit, 0, 0, null)))
            {
                // the loop is not reachable; give the exit event a moment, then treat it as stuck
            }

            if (!s.Exited.Wait(_options.StopTimeout))
            {
                _log("Hook thread did not stop in time; abandoning it (poll-only from now on).");
                AbandonSession(s);
                return false;
            }
        }

        lock (_gate)
        {
            if (ReferenceEquals(_session, s))
            {
                _session = null;
            }
        }

        return true;
    }

    public void Dispose() => Stop();

    // ---- everything below runs on the hook thread ---------------------------------------------------

    private void Run(Session s)
    {
        try
        {
            s.ManagedThreadId = Environment.CurrentManagedThreadId;
            MessageLoopNative.PeekMessageW(out _, 0, 0, 0, MessageLoopNative.PM_NOREMOVE);   // creates the thread's queue
            Volatile.Write(ref s.ThreadId, MessageLoopNative.GetCurrentThreadId());

            DrainCommands(s);                                                                 // the pre-queued Install
            while (MessageLoopNative.GetMessageW(out var msg, 0, 0, 0) > 0)
            {
                if (msg.message == WmCommand)
                {
                    DrainCommands(s);
                }
                else
                {
                    MessageLoopNative.TranslateMessage(in msg);
                    MessageLoopNative.DispatchMessageW(in msg);
                }
            }
        }
        catch (Exception ex)
        {
            _log($"Hook thread failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try
            {
                RemoveAll(s);
            }
            catch
            {
                // nothing more to do; the system unhooks when the thread ends
            }

            FailPending(s);
            if (Interlocked.Exchange(ref s.SlotHeld, 0) == 1)
            {
                _guard.ReleaseThread(TrackerThreadKind.Hook);
            }

            s.Exited.Set();
        }
    }

    private void DrainCommands(Session s)
    {
        while (s.Commands.TryDequeue(out var c))
        {
            switch (c.Kind)
            {
                case CommandKind.Install:
                    c.Ack?.TrySetResult(Install(s));
                    break;
                case CommandKind.SetScope:
                    ApplyScope(s, c.Pid, c.Sequence);
                    break;
                case CommandKind.Uninstall:
                    RemoveAll(s);
                    c.Ack?.TrySetResult(true);
                    break;
                case CommandKind.Quit:
                    RemoveAll(s);
                    MessageLoopNative.PostQuitMessage(0);
                    break;
            }
        }
    }

    private bool Install(Session s)
    {
        if (s.ForegroundHook != 0)
        {
            return true;                                   // installing twice is harmless
        }

        nint h = _api.SetHook(WinEventMap.SystemForeground, WinEventMap.SystemForeground, 0, _callback);
        if (h == 0)
        {
            return false;
        }

        s.ForegroundHook = h;
        Volatile.Write(ref s.GlobalHooks, 1);
        return true;
    }

    private void ApplyScope(Session s, uint pid, long sequence)
    {
        if (sequence < s.AppliedSequence)
        {
            return;                                        // a newer request has already been applied (04 J, H8)
        }

        s.AppliedSequence = sequence;
        if (s.ForegroundHook == 0)
        {
            return;                                        // not installed (or already removed): nothing to scope
        }

        if (pid == s.ScopePid && (pid == 0 || s.Scoped.Count > 0))
        {
            return;                                        // already listening to exactly this process: a replacement worker asks again, nothing to do
        }

        RemoveScoped(s);
        Volatile.Write(ref s.ScopePid, pid);
        if (pid == 0)
        {
            return;
        }

        foreach (var (min, max) in WinEventMap.ScopedRanges)
        {
            nint h = _api.SetHook(min, max, pid, _callback);
            if (h == 0)
            {
                _log($"Scoped hook {min:X}-{max:X} could not be installed; the poll covers it.");
                continue;
            }

            s.Scoped.Add(h);
        }

        Volatile.Write(ref s.ScopedHooks, s.Scoped.Count);
    }

    private void RemoveScoped(Session s)
    {
        foreach (var h in s.Scoped)
        {
            _api.Unhook(h);
        }

        s.Scoped.Clear();
        Volatile.Write(ref s.ScopedHooks, 0);
        Volatile.Write(ref s.ScopePid, 0);
    }

    private void RemoveAll(Session s)
    {
        RemoveScoped(s);
        if (s.ForegroundHook != 0)
        {
            _api.Unhook(s.ForegroundHook);
            s.ForegroundHook = 0;
            Volatile.Write(ref s.GlobalHooks, 0);
        }
    }

    private static void FailPending(Session s)
    {
        while (s.Commands.TryDequeue(out var c))
        {
            c.Ack?.TrySetResult(false);
        }
    }

    /// <summary>
    /// The WinEvent callback (04 B.7). Trivial on purpose: a parameter compare, a table lookup and
    /// <see cref="SignalRouter.Raise"/> (an <c>Interlocked</c> OR and at most one post). No COM, no file system, no UI,
    /// no logging, no allocation, never throws.
    /// </summary>
    private void OnWinEvent(nint hook, uint eventId, nint hwnd, int idObject, int idChild, uint threadId, uint time)
    {
        const int ObjidWindow = 0;
        if (idObject != ObjidWindow)
        {
            return;
        }

        var signal = WinEventMap.ToSignal(eventId);
        if (signal != TrackerSignal.None)
        {
            _router.Raise(signal);
        }
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    private static bool Post(Session s, Command c)
    {
        s.Commands.Enqueue(c);
        uint tid = Volatile.Read(ref s.ThreadId);
        if (tid == 0)
        {
            return true;                                   // the thread has not published its id yet; it drains the queue on start
        }

        return MessageLoopNative.PostThreadMessageW(tid, WmCommand, 0, 0);
    }

    private void AbandonSession(Session s)
    {
        s.Retired = true;
        Abandoned = true;
        if (Interlocked.Exchange(ref s.SlotHeld, 0) == 1)
        {
            _guard.AbandonThread(TrackerThreadKind.Hook);
        }

        lock (_gate)
        {
            if (ReferenceEquals(_session, s))
            {
                _session = null;
            }
        }
    }
}
