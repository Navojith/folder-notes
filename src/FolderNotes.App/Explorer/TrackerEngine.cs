using System.Diagnostics;
using FolderNotes.App.Explorer.Com;
using FolderNotes.App.Explorer.Native;

namespace FolderNotes.App.Explorer;

/// <summary>Asks the hook thread to listen to one process (0 = none). The engine never touches a hook handle (04 B.7, L).</summary>
internal interface IHookScope
{
    bool SetScope(uint processId);
}

/// <summary>
/// The scope channel of one worker generation: forwards only while that generation is the current one, so an abandoned
/// worker that wakes up late cannot re-scope the hooks (04 B.6 (3), B.7).
/// </summary>
internal sealed class GenerationScope(int generation, StatePublisher publisher, IHookScope inner) : IHookScope
{
    public bool SetScope(uint processId) => publisher.Generation == generation && inner.SetScope(processId);
}

/// <summary>
/// The evaluation, the cache and the recovery of 04 L, as one synchronous object that runs <b>only on the COM worker
/// thread</b> and owns the gateway and the <see cref="TabMap{THandle}"/>. It has no thread and no timer of its own: the
/// host feeds it signals and a <see cref="TimeProvider"/>, calls <see cref="Tick"/>, and sleeps for what <see cref="Tick"/>
/// returns. That keeps it testable with a fake Explorer and a fake clock.
/// <para>
/// A worker gets a new engine (empty cache) with its own <c>generation</c>; everything it publishes carries it, so a
/// zombie's results are dropped by the <see cref="StatePublisher"/> (04 B.6). Nothing here throws out of
/// <see cref="Tick"/>: an unexpected exception becomes a transient result and a log line.
/// </para>
/// </summary>
internal sealed class TrackerEngine : IDisposable
{
    /// <summary>At most this many evaluations per wake-up, so a stream of due work never starves the worker's message loop.</summary>
    public const int MaxStepsPerTick = 8;

    /// <summary>A suspect (empty) root is replaced at most this many times in a row; then it is accepted (04 L, L7 against L8).</summary>
    public const int MaxRootReplacements = 2;

    private readonly int _generation;
    private readonly IShellWindowsGatewayFactory _factory;
    private readonly IWin32Shell _win;
    private readonly StatePublisher _publisher;
    private readonly TrackerStateMachine _machine;
    private readonly IHookScope? _scope;
    private readonly TrackerCounters _counters;
    private readonly TimeProvider _clock;
    private readonly Action<string> _log;
    private readonly EvaluationScheduler _scheduler;
    private readonly TabMap<TabHandle> _map = new();
    private readonly RootValidator _validator = new();
    private readonly CallJournal _journal;
    private IShellWindowsGateway? _gateway;
    private uint _shellPid;
    private uint _scopePid;
    private nint _lastFrame;
    private nint _unresolvedFrame;
    private int _rootReplacements;
    private bool _inRecovery;
    private long _recoveryDueTimestamp;
    private bool _disposed;

    public TrackerEngine(
        int generation,
        IShellWindowsGatewayFactory factory,
        IWin32Shell win,
        StatePublisher publisher,
        TrackerStateMachine machine,
        IHookScope? scope,
        TrackerCounters counters,
        TimeProvider clock,
        SchedulerOptions? schedulerOptions = null,
        Action<string>? log = null,
        CallJournal? journal = null)
    {
        _generation = generation;
        _journal = journal ?? new CallJournal(generation, clock);
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _win = win ?? throw new ArgumentNullException(nameof(win));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _machine = machine ?? throw new ArgumentNullException(nameof(machine));
        _scope = scope;
        _counters = counters ?? throw new ArgumentNullException(nameof(counters));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _log = log ?? (_ => { });
        _scheduler = new EvaluationScheduler(clock, schedulerOptions, publisher.NextSequence);
    }

    /// <summary>True while waiting to recreate <c>IShellWindows</c> (04 F).</summary>
    public bool Recovering => _inRecovery;

    public int TabEntries => _map.Count;

    public bool Connected => _gateway is not null;

    public uint ScopePid => _scopePid;

    public bool FrameMarkedUnresolved => _unresolvedFrame != 0;

    /// <summary>Connect to <c>IShellWindows</c> (or begin recovery if that is not possible yet) and ask for a first evaluation.</summary>
    public void Start()
    {
        if (TryConnect())
        {
            _scheduler.Signal(SignalKinds.Foreground);
        }
        else
        {
            EnterRecovery();
        }
    }

    /// <summary>The hook thread's bits, reduced to the scheduler's kinds (04 L). A new Foreground ends the "unresolved" memory of a frame.</summary>
    public void OnSignals(TrackerSignal bits)
    {
        if (bits == TrackerSignal.None || _disposed)
        {
            return;
        }

        var kinds = SignalKinds.None;
        if (bits.HasFlag(TrackerSignal.Foreground))
        {
            kinds |= SignalKinds.Foreground;
        }

        if (bits.HasFlag(TrackerSignal.NameChange) || bits.HasFlag(TrackerSignal.ShowHide) || bits.HasFlag(TrackerSignal.Minimize))
        {
            kinds |= SignalKinds.Navigation;
        }

        if (bits.HasFlag(TrackerSignal.CreateDestroy))
        {
            kinds |= SignalKinds.Structure;
            if (_gateway is not null)
            {
                using var op = _journal.Scope(CallSite.Release);
                _map.SweepDead(_win.IsWindow);               // 04 C: a destroyed window's entry is released at once
            }
        }

        if (kinds != SignalKinds.None)
        {
            _scheduler.Signal(kinds);
        }
    }

    /// <summary><c>TaskbarCreated</c> forwarded by the tray window (04 F): rebuild everything, no matter what else looks fine.</summary>
    public void OnShellRestarted()
    {
        if (!_disposed && _gateway is not null)
        {
            EnterRecovery();
        }
    }

    /// <summary>
    /// Runs everything that is due (recovery attempts and evaluations), at most <see cref="MaxStepsPerTick"/> of them.
    /// Returns how long the host may sleep before calling again: <see cref="TimeSpan.Zero"/> means "more is due, post
    /// another wake-up", null means nothing is scheduled.
    /// </summary>
    public TimeSpan? Tick()
    {
        if (_disposed)
        {
            return null;
        }

        for (int step = 0; step < MaxStepsPerTick; step++)
        {
            if (_gateway is null)
            {
                if (!_inRecovery)
                {
                    return null;
                }

                var wait = RecoveryRemaining();
                if (wait > TimeSpan.Zero)
                {
                    return wait;
                }

                AttemptRecovery();
                continue;
            }

            if (!_scheduler.TryTakeDue(out var request))
            {
                return _scheduler.TimeUntilNextDue();
            }

            RunEvaluation(request);
        }

        return TimeSpan.Zero;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        using var op = _journal.Scope(CallSite.Release);
        _map.Dispose();
        _gateway?.Dispose();
        _gateway = null;
    }

    // ---- evaluation ------------------------------------------------------------------------------------

    private void RunEvaluation(EvaluationRequest request)
    {
        long start = Stopwatch.GetTimestamp();
        EvaluationOutcome outcome;
        try
        {
            outcome = Evaluate(request);
        }
        catch (Exception ex)
        {
            _log($"Tracker evaluation failed: {ex.GetType().Name}: {ex.Message}");
            _publisher.TryPublish(_generation, request.Sequence, null);
            outcome = new EvaluationOutcome(_lastFrame != 0, false, EvaluationResultKind.Transient);
        }

        _counters.RecordEvaluation(Stopwatch.GetElapsedTime(start));
        var advice = _scheduler.Complete(request.Sequence, outcome);
        if (advice.LadderExhausted && _lastFrame != 0)
        {
            _unresolvedFrame = _lastFrame;                    // 04 G L8: not re-read until a signal, so no retry loop
        }
    }

    private EvaluationOutcome Evaluate(EvaluationRequest request)
    {
        nint shell = _win.GetShellWindow();
        if (shell == 0 || (_shellPid != 0 && _win.ProcessIdOf(shell) != _shellPid))
        {
            EnterRecovery();                                  // the shell is gone or was replaced (04 F detector 3 and 4)
            return new EvaluationOutcome(false, false, EvaluationResultKind.Resolved);
        }

        nint foreground = _win.GetForegroundWindow();
        nint frame = ForegroundResolver.Resolve(foreground, _win.IsExplorerFrame, _win.OwnerOf, _win.IsMinimized);
        _lastFrame = frame;
        if (frame == 0)
        {
            SetScope(0);
            _unresolvedFrame = 0;
            _publisher.TryPublish(_generation, request.Sequence, ActiveFolderState.Inactive());
            return new EvaluationOutcome(false, false, EvaluationResultKind.Resolved);
        }

        SetScope(_win.ProcessIdOf(frame));
        if (_unresolvedFrame != 0 && _unresolvedFrame != frame)
        {
            _unresolvedFrame = 0;
        }

        if (_unresolvedFrame == frame && request.Signals == SignalKinds.None)
        {
            return new EvaluationOutcome(true, false, EvaluationResultKind.Resolved);   // L8: only a signal re-reads it
        }

        var active = ActiveTabResolver.Resolve(frame, _win.TabChildren(frame));
        if (active.Kind == ActiveTabKind.Unresolved)
        {
            _publisher.TryPublish(_generation, request.Sequence, null);
            return new EvaluationOutcome(true, false, EvaluationResultKind.Unresolved);
        }

        if (_win.IsHung(frame))
        {
            // 04 § M.9: the frame has not pumped for ~5 s; a call into it would wedge this worker. Retry later instead.
            _counters.CountPreScreened();
            _publisher.TryPublish(_generation, request.Sequence, null);
            return new EvaluationOutcome(true, false, EvaluationResultKind.Transient);
        }

        TabEntry<TabHandle>? entry = null;
        bool cached = !request.Rebuild && _map.TryGet(active.Key, out entry);
        if (!cached)
        {
            var rebuilt = Rebuild(frame);
            if (_gateway is null)
            {
                return new EvaluationOutcome(false, false, EvaluationResultKind.Resolved);   // recovery was entered (Inactive published)
            }

            if (rebuilt == EvaluationResultKind.Transient || !_map.TryGet(active.Key, out entry))
            {
                _publisher.TryPublish(_generation, request.Sequence, null);
                return new EvaluationOutcome(true, false, rebuilt == EvaluationResultKind.Transient ? EvaluationResultKind.Transient : EvaluationResultKind.Unresolved);
            }
        }

        PathReadResult read;
        using (_journal.Scope(CallSite.PathRead, _win.ProcessIdOf(frame), frame))
        {
            read = _gateway!.TryReadPath(entry!.Handle);
        }

        if (read.Hr < 0)
        {
            return HandleReadFailure(request, frame, active.Key, read.Hr);
        }

        _map.RecordSuccess(active.Key, read.Path);
        _unresolvedFrame = 0;
        var result = _publisher.TryPublish(_generation, request.Sequence, new ActiveFolderState(frame, active.Key, read.Path, 0));
        if (result == PublishResult.Published)
        {
            _counters.CountPublished();
        }

        return new EvaluationOutcome(true, result == PublishResult.Published, EvaluationResultKind.Resolved);
    }

    private EvaluationOutcome HandleReadFailure(EvaluationRequest request, nint frame, nint key, int hr)
    {
        bool alive = _win.IsWindow(key);
        var cls = ComFailureClassifier.Classify(hr, windowAlive: alive);
        if (cls == ComFailureClass.Gone && alive)
        {
            EnterRecovery();                                  // the window exists but its server is dead: the shell died under us
            return new EvaluationOutcome(false, false, EvaluationResultKind.Resolved);
        }

        using (_journal.Scope(CallSite.Release))
        {
            _map.RecordFailure(key, cls);                     // Gone drops the tab; Other drops it after three in a row; Transient keeps it
        }

        _publisher.TryPublish(_generation, request.Sequence, null);
        return new EvaluationOutcome(true, false, cls == ComFailureClass.Other ? EvaluationResultKind.Unresolved : EvaluationResultKind.Transient);
    }

    /// <summary>One enumeration of <c>IShellWindows</c> (04 C). Enters recovery if the root is gone or is a private empty server.</summary>
    private EvaluationResultKind Rebuild(nint foregroundFrame)
    {
        if (_win.AnyExplorerFrameHung())
        {
            _counters.CountPreScreened();                     // 04 § M.9: an enumeration calls every Explorer process; defer it
            return EvaluationResultKind.Transient;
        }

        _counters.CountRebuild();
        var found = new List<DiscoveredTab<TabHandle>>();
        int hr;
        using (_journal.Scope(CallSite.Enumerate, _shellPid))
        {
            hr = _gateway!.TryEnumerateTabs(found);
        }

        if (hr < 0)
        {
            DisposeAll(found);
            if (ComFailureClassifier.Classify(hr) == ComFailureClass.Gone)
            {
                EnterRecovery();
            }

            return EvaluationResultKind.Transient;
        }

        if (ShouldReplaceRoot(found.Count))
        {
            DisposeAll(found);
            _log("IShellWindows enumerates nothing although Explorer windows exist; replacing the root (04 F, L7).");
            EnterRecovery();
            return EvaluationResultKind.Transient;
        }

        using (_journal.Scope(CallSite.Release))
        {
            _map.Reconcile(found, foregroundFrame);
        }

        if (found.Count > 0 || !_win.AnyExplorerFrameExists())
        {
            _counters.CountFullEnumeration();                 // a validated enumeration answered: evidence for a trial worker (04 § M.5)
        }

        return EvaluationResultKind.Resolved;
    }

    /// <summary>True when the root should be discarded after an in-evaluation rebuild (two empty looks while frames exist, and not yet replaced twice).</summary>
    private bool ShouldReplaceRoot(int enumerated)
    {
        var verdict = _validator.Observe(enumerated, _win.AnyExplorerFrameExists());
        if (enumerated > 0)
        {
            _rootReplacements = 0;
            return false;
        }

        if (verdict == RootVerdict.ReplaceRoot && _rootReplacements < MaxRootReplacements)
        {
            _rootReplacements++;
            return true;
        }

        return false;
    }

    /// <summary>For a freshly created root: reject it while it looks like a private empty server, until the replacement limit is reached.</summary>
    private bool ShouldRejectRoot(int enumerated)
    {
        var verdict = _validator.Observe(enumerated, _win.AnyExplorerFrameExists());
        if (verdict == RootVerdict.Ok)
        {
            _rootReplacements = 0;
            return false;
        }

        if (_rootReplacements >= MaxRootReplacements)
        {
            return false;                                     // replaced enough times: the windows are probably elevated (L8)
        }

        if (verdict == RootVerdict.ReplaceRoot)
        {
            _rootReplacements++;
        }

        return true;
    }

    private void DisposeAll(List<DiscoveredTab<TabHandle>> found)
    {
        using var op = _journal.Scope(CallSite.Release);
        foreach (var tab in found)
        {
            tab.Handle.Dispose();
        }

        found.Clear();
    }

    private void SetScope(uint pid)
    {
        if (pid == _scopePid)
        {
            return;
        }

        _scopePid = pid;
        _scope?.SetScope(pid);
    }

    // ---- recovery (04 F) --------------------------------------------------------------------------------

    private void EnterRecovery()
    {
        if (_machine.State == TrackerState.Running)
        {
            _machine.ShellGone();                             // publishes Inactive and moves to Recovering
        }

        using (_journal.Scope(CallSite.Release))
        {
            _map.Clear();
            _gateway?.Dispose();
            _gateway = null;
        }

        _shellPid = 0;
        _unresolvedFrame = 0;
        _validator.Reset();
        SetScope(0);
        _inRecovery = true;
        _counters.CountRecovery();
        ScheduleNextAttempt();
    }

    private void ScheduleNextAttempt()
    {
        var delay = _machine.NextRecoveryDelay();
        _recoveryDueTimestamp = _clock.GetTimestamp() + (long)(delay.TotalSeconds * _clock.TimestampFrequency);
    }

    private TimeSpan RecoveryRemaining()
    {
        long left = _recoveryDueTimestamp - _clock.GetTimestamp();
        return left <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds((double)left / _clock.TimestampFrequency);
    }

    private void AttemptRecovery()
    {
        // Never create ShellWindows while the shell is down: COM would start a private empty server in rundll32 (04 F, L7).
        if (TryConnect())
        {
            _inRecovery = false;
            _machine.RecoverySucceeded();
            _scheduler.Signal(SignalKinds.Foreground | SignalKinds.Structure);
            return;
        }

        ScheduleNextAttempt();
    }

    /// <summary>Creates and validates the root and fills the map. False = not usable yet (nothing is kept).</summary>
    private bool TryConnect()
    {
        nint shell = _win.GetShellWindow();
        if (shell == 0)
        {
            return false;
        }

        if (_win.AnyExplorerFrameHung())
        {
            _counters.CountPreScreened();                     // 04 § M.9: the first enumeration is deferred to the recovery back-off
            return false;
        }

        uint shellPid = _win.ProcessIdOf(shell);
        int hr;
        IShellWindowsGateway? gateway;
        using (_journal.Scope(CallSite.RootCreate, shellPid))
        {
            hr = _factory.TryCreate(out gateway);
            if (hr < 0 || gateway is null)
            {
                gateway?.Dispose();
                return false;
            }
        }

        if (gateway is IJournaledGateway journaled)
        {
            journaled.AttachJournal(_journal, _win.ProcessIdOf);
        }

        var found = new List<DiscoveredTab<TabHandle>>();
        using (_journal.Scope(CallSite.Enumerate, shellPid))
        {
            hr = gateway.TryEnumerateTabs(found);
        }

        bool usable = hr >= 0 && !ShouldRejectRoot(found.Count);
        if (!usable)
        {
            DisposeAll(found);
            using (_journal.Scope(CallSite.Release, shellPid))
            {
                gateway.Dispose();
            }

            return false;
        }

        _gateway = gateway;
        _shellPid = shellPid;
        using (_journal.Scope(CallSite.Release))
        {
            _map.Reconcile(found, 0);
        }

        _counters.CountFullEnumeration();                     // created, enumerated and validated: evidence for a trial worker (04 § M.5)
        return true;
    }
}
