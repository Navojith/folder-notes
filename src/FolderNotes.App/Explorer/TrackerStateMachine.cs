using System.Runtime.CompilerServices;

namespace FolderNotes.App.Explorer;

internal enum TrackerState
{
    Stopped,
    Running,

    /// <summary>The shell is gone or its root object is wrong: waiting to recreate it (04 F).</summary>
    Recovering,

    /// <summary>
    /// A worker was abandoned and its server is unresolved (04 § M): tracking is suspended for every Explorer window. No worker
    /// runs, except one trial worker (<see cref="QuarantinePhase.Trial"/>).
    /// </summary>
    Quarantined,

    /// <summary>Latched off by the circuit breaker or the thread cap; only <see cref="TrackerStateMachine.Retry"/> leaves it.</summary>
    Disabled,
}

/// <summary>The sub-state of <see cref="TrackerState.Quarantined"/> (04 § M.6).</summary>
internal enum QuarantinePhase
{
    None,

    /// <summary>No worker; the monitor looks for evidence every second.</summary>
    Waiting,

    /// <summary>One trial worker runs; the quarantine record is still in force until it passes (04 § M.5).</summary>
    Trial,
}

internal enum TrialKind
{
    /// <summary>Started by the monitor after a complete health hold, at most once per suspect server.</summary>
    Automatic,

    /// <summary>Started by the user's Retry while no hang evidence is seen.</summary>
    Explicit,
}

internal enum DisabledReason
{
    None,
    CircuitBreaker,
    ThreadCap,
}

/// <summary>What the user is told (04 § M.7). One value per situation the Settings card must tell apart.</summary>
internal enum TrackerAvailability
{
    Stopped,

    /// <summary>Running normally.</summary>
    Tracking,

    /// <summary>The shell is down or restarting (04 F): resumes by itself.</summary>
    TemporarilyUnavailable,

    /// <summary>Waiting for evidence that an unresponsive Explorer recovered (04 § M).</summary>
    Quarantined,

    /// <summary>A trial worker is trying again.</summary>
    RecoveryAttempt,

    /// <summary>The circuit breaker tripped: Retry offered.</summary>
    Paused,

    /// <summary>The lifetime thread cap is used up: off until the app restarts, no Retry.</summary>
    PermanentlyOff,
}

internal enum StartResult
{
    Started,

    /// <summary>Started, but an unresolved quarantine is in force (04 § M.6 Q17): hooks and monitor run, no worker yet.</summary>
    Quarantined,

    /// <summary>Another tracker is active in this process (04 B.6: at most one).</summary>
    AnotherTrackerActive,

    /// <summary>Disabled: only an explicit <see cref="TrackerStateMachine.Retry"/> restarts it.</summary>
    Disabled,

    /// <summary>Already running.</summary>
    AlreadyRunning,
}

internal enum RetryResult
{
    /// <summary>A normal worker was started (no quarantine in force).</summary>
    Started,

    /// <summary>An explicit trial worker was started; the quarantine stays in force until it passes (04 § M.6 Q21/Q22).</summary>
    TrialStarted,

    NotDisabled,

    /// <summary>The lifetime thread cap is reached: stays disabled until the app restarts.</summary>
    ThreadCapReached,

    /// <summary>A trial worker is already running (at most one at a time).</summary>
    TrialInProgress,

    /// <summary>A suspect server shows hang evidence right now: a trial would hit the same frozen server.</summary>
    ServerNotResponding,
}

internal enum WedgeOutcome
{
    /// <summary>No replacement: the tracker is quarantined until there is evidence (04 § M).</summary>
    Quarantined,

    /// <summary>The breaker or the cap latched the tracker off.</summary>
    Disabled,
}

internal readonly record struct WedgeDecision(WedgeOutcome Outcome, int Generation, DisabledReason Reason);

/// <summary>What the monitor must do after one look (04 § M.6).</summary>
internal enum QuarantineAction
{
    None,

    /// <summary>Every entry resolved while Waiting: start one normal worker (its slot is already reserved).</summary>
    StartWorker,

    /// <summary>An automatic trial was granted: start the trial worker (its slot is already reserved).</summary>
    StartTrial,

    /// <summary>The trial reached its deadline without evidence: stop it, then report <see cref="TrackerStateMachine.TrialStopped"/>.</summary>
    StopTrial,
}

internal sealed record TrackerLimits
{
    public static TrackerLimits Default { get; } = new();

    /// <summary>Wedges inside <see cref="BreakerWindow"/> that trip the breaker (04 B.6: 3).</summary>
    public int BreakerWedges { get; init; } = 3;

    /// <summary>A wedge counts while its age is below this (so after 10 quiet minutes it has expired).</summary>
    public TimeSpan BreakerWindow { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Most COM workers ever abandoned in this process (04 B.6, decision Q1: 6). The breaker trips at 3, so the cap
    /// allows two breaker episodes: one explicit Retry works, then the tracker stays off until the app restarts.
    /// Retry never resets the count, and the count is process-wide (<see cref="TrackerProcessGuard"/>).
    /// </summary>
    public int MaxAbandonedThreads { get; init; } = TrackerThreadBudget.DefaultMaxAbandonedWorkers;
}

/// <summary>The kinds of thread the tracker may own (04 B.6 thread budget).</summary>
internal enum TrackerThreadKind
{
    /// <summary>The one STA thread that owns every COM object.</summary>
    Worker,

    /// <summary>The COM-free thread that owns the WinEvent hooks (04 B.7).</summary>
    Hook,

    /// <summary>The thread that cancels and abandons a stuck worker.</summary>
    Watchdog,
}

/// <summary>Process-wide thread limits per kind (04 B.6): live threads and lifetime abandoned threads.</summary>
internal sealed record TrackerThreadBudget
{
    public static TrackerThreadBudget Default { get; } = new();

    public int MaxLiveWorkers { get; init; } = 1;

    public int MaxLiveHook { get; init; } = 1;

    public int MaxLiveWatchdog { get; init; } = 1;

    /// <summary>Default lifetime cap on abandoned workers (Q1). The policy lives in <see cref="TrackerLimits.MaxAbandonedThreads"/>.</summary>
    public const int DefaultMaxAbandonedWorkers = 6;

    /// <summary>Lifetime cap on abandoned hook threads: an abandoned hook thread is never replaced (04 B.7).</summary>
    public int MaxAbandonedHook { get; init; } = 1;

    /// <summary>Most threads the tracker owns at once when nothing has wedged: 3.</summary>
    public int MaxLiveTotal => MaxLiveWorkers + MaxLiveHook + MaxLiveWatchdog;

    /// <summary>Most threads the tracker can own at any moment: 3 live + 6 abandoned workers + 1 abandoned hook = 10.</summary>
    public int MaxOwnedAtOnce => MaxLiveTotal + DefaultMaxAbandonedWorkers + MaxAbandonedHook;
}

/// <summary>
/// Process-wide limits that must hold across tracker instances (04 B.6): one active tracker, and a lifetime cap
/// on abandoned threads. Production uses <see cref="Shared"/>; tests make their own.
/// </summary>
internal sealed class TrackerProcessGuard
{
    private readonly Lock _gate = new();
    private readonly TrackerThreadBudget _budget;
    private readonly int[] _live = new int[3];
    private object? _owner;
    private int _abandoned;
    private int _abandonedHook;

    public TrackerProcessGuard(TrackerThreadBudget? budget = null, QuarantineRecord? quarantine = null)
    {
        _budget = budget ?? TrackerThreadBudget.Default;
        Quarantine = quarantine ?? new QuarantineRecord();
    }

    /// <summary>
    /// The quarantine record (04 § M): process-wide like the cap, so Stop/Start and a new tracker instance cannot bypass it.
    /// </summary>
    public QuarantineRecord Quarantine { get; }

    public static TrackerProcessGuard Shared { get; } = new();

    public TrackerThreadBudget Budget => _budget;

    /// <summary>Workers abandoned so far in this process; never reset (not even by Retry).</summary>
    public int AbandonedThreads
    {
        get { lock (_gate) { return _abandoned; } }
    }

    public int AbandonedHookThreads
    {
        get { lock (_gate) { return _abandonedHook; } }
    }

    public int LiveThreads(TrackerThreadKind kind)
    {
        lock (_gate) { return _live[(int)kind]; }
    }

    /// <summary>Live threads of every kind plus every abandoned one: what the tracker owns right now.</summary>
    public int OwnedThreads
    {
        get { lock (_gate) { return _live.Sum() + _abandoned + _abandonedHook; } }
    }

    /// <summary>Reserve a slot before creating a thread. False means the kind is at its limit: do not create it.</summary>
    public bool TryReserveThread(TrackerThreadKind kind)
    {
        lock (_gate)
        {
            if (_live[(int)kind] >= LiveLimit(kind))
            {
                return false;
            }

            if (kind == TrackerThreadKind.Hook && _abandonedHook >= _budget.MaxAbandonedHook)
            {
                return false; // an abandoned hook thread is never replaced in this process
            }

            _live[(int)kind]++;
            return true;
        }
    }

    /// <summary>The thread ended (or was never created): free its slot.</summary>
    public void ReleaseThread(TrackerThreadKind kind)
    {
        lock (_gate)
        {
            if (_live[(int)kind] > 0)
            {
                _live[(int)kind]--;
            }
        }
    }

    /// <summary>
    /// The thread is stuck and left behind on purpose: its slot is freed (it no longer serves the tracker) but it
    /// counts against the lifetime cap of its kind. Returns the abandoned count for the kind.
    /// </summary>
    public int AbandonThread(TrackerThreadKind kind)
    {
        lock (_gate)
        {
            if (_live[(int)kind] > 0)
            {
                _live[(int)kind]--;
            }

            return kind switch
            {
                TrackerThreadKind.Worker => ++_abandoned,
                TrackerThreadKind.Hook => ++_abandonedHook,
                _ => 0, // a watchdog is never abandoned (it is not a COM thread); its slot is simply freed
            };
        }
    }

    private int LiveLimit(TrackerThreadKind kind) => kind switch
    {
        TrackerThreadKind.Worker => _budget.MaxLiveWorkers,
        TrackerThreadKind.Hook => _budget.MaxLiveHook,
        _ => _budget.MaxLiveWatchdog,
    };

    public bool TryAcquire(object owner)
    {
        lock (_gate)
        {
            if (_owner is null || ReferenceEquals(_owner, owner))
            {
                _owner = owner;
                return true;
            }

            return false;
        }
    }

    public void Release(object owner)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_owner, owner))
            {
                _owner = null;
            }
        }
    }

}

/// <summary>
/// Lifecycle rules of 04 § ExplorerTracker design B.6, F and M as a pure state machine: Stopped → Running ⇄ Recovering,
/// wedge → Quarantined (Waiting ⇄ Trial) → Running, or Disabled by the breaker or the cap. It never starts a thread itself;
/// it tells the host what is allowed and reserves the worker slot first.
/// <para>
/// Thread limits: <see cref="LiveWorkers"/> is 0 or 1 (a trial and a normal worker share the one slot), the cap on abandoned
/// threads and the quarantine record are process-wide, and <see cref="OnWedge"/> invalidates the publisher's generation
/// (publishing Inactive) before anything else, so an abandoned worker can never publish.
/// </para>
/// </summary>
internal sealed class TrackerStateMachine
{
    // Thread safety: the worker thread (recovery), the watchdog thread (OnWedge, the monitor) and the UI thread (Start/Stop/Retry)
    // all call this. Every mutating method is [Synchronized] (locks this; the machine is never exposed outside the tracker), and
    // the state properties are single enum/int reads, atomic on x64. It calls StatePublisher and QuarantineRecord (their own
    // locks) but neither calls back, so the lock order is always machine → publisher/record.
    private static readonly TimeSpan[] RecoveryDelays =
        [TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)];

    private static readonly TimeSpan RecoverySteady = TimeSpan.FromSeconds(5);

    private readonly TimeProvider _clock;
    private readonly TrackerLimits _limits;
    private readonly TrackerProcessGuard _guard;
    private readonly StatePublisher _publisher;
    private readonly Queue<long> _wedges = new();
    private int _recoveryAttempt;
    private long _trialStartedAt;
    private long _trialBaseline;

    public TrackerStateMachine(TimeProvider clock, StatePublisher publisher, TrackerProcessGuard guard, TrackerLimits? limits = null)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _guard = guard ?? throw new ArgumentNullException(nameof(guard));
        _limits = limits ?? TrackerLimits.Default;
        ArgumentOutOfRangeException.ThrowIfLessThan(_limits.BreakerWedges, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(_limits.MaxAbandonedThreads, 1);
    }

    public TrackerState State { get; private set; } = TrackerState.Stopped;

    public DisabledReason DisabledReason { get; private set; }

    /// <summary>Only meaningful while <see cref="TrackerState.Quarantined"/>.</summary>
    public QuarantinePhase Phase { get; private set; }

    /// <summary>The kind of the running trial (meaningful while <see cref="Phase"/> is Trial).</summary>
    public TrialKind TrialKind { get; private set; }

    /// <summary>Worker threads currently serving the tracker: 0 or 1.</summary>
    public int LiveWorkers { get; private set; }

    /// <summary>The process-wide quarantine record (04 § M).</summary>
    public QuarantineRecord Quarantine => _guard.Quarantine;

    public int WedgesInWindow
    {
        get
        {
            lock (this)
            {
                Expire();
                return _wedges.Count;
            }
        }
    }

    /// <summary>What the user is told (04 § M.7).</summary>
    public TrackerAvailability Availability => State switch
    {
        TrackerState.Running => TrackerAvailability.Tracking,
        TrackerState.Recovering => TrackerAvailability.TemporarilyUnavailable,
        TrackerState.Quarantined => Phase == QuarantinePhase.Trial ? TrackerAvailability.RecoveryAttempt : TrackerAvailability.Quarantined,
        TrackerState.Disabled => DisabledReason == DisabledReason.ThreadCap ? TrackerAvailability.PermanentlyOff : TrackerAvailability.Paused,
        _ => TrackerAvailability.Stopped,
    };

    /// <summary>
    /// Stopped → Running, or → Quarantined.Waiting when the process-wide record is unresolved (04 § M.6 Q17/Q18; the host checks
    /// E1/E2 just before). The host then starts the one worker only for <see cref="StartResult.Started"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public StartResult Start()
    {
        switch (State)
        {
            case TrackerState.Disabled:
                return StartResult.Disabled;
            case TrackerState.Running or TrackerState.Recovering or TrackerState.Quarantined:
                return StartResult.AlreadyRunning;
        }

        if (_guard.AbandonedThreads >= _limits.MaxAbandonedThreads)
        {
            return StartResult.Disabled;                   // the lifetime cap was used up (even via Stop): off for this process
        }

        if (!_guard.TryAcquire(this))
        {
            return StartResult.AnotherTrackerActive;
        }

        if (_guard.Quarantine.HasUnresolved)
        {
            State = TrackerState.Quarantined;              // Stop/Start and new instances meet the same record (Q17)
            Phase = QuarantinePhase.Waiting;
            _guard.Quarantine.RestartHold();               // a full hold is needed again: nobody watched while stopped
            return StartResult.Quarantined;
        }

        if (!_guard.TryReserveThread(TrackerThreadKind.Worker))
        {
            _guard.Release(this);
            return StartResult.AnotherTrackerActive; // defensive: the process budget says a worker is already live
        }

        State = TrackerState.Running;
        LiveWorkers = 1;
        return StartResult.Started;
    }

    /// <summary>
    /// Any state → Stopped (a Disabled tracker stays Disabled and keeps the process slot: the latch is not an escape hatch,
    /// and no second tracker may start in its place), publishes Inactive, and frees the process slot otherwise. The quarantine
    /// record is kept (Q15/Q16). The host ends the worker (bounded wait).
    /// </summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public void Stop(bool workerAbandoned = false)
    {
        if (LiveWorkers == 1)
        {
            // A worker that would not end within the bounded wait is left behind: it still counts against the cap (04 B.6).
            if (workerAbandoned)
            {
                _guard.AbandonThread(TrackerThreadKind.Worker);
            }
            else
            {
                _guard.ReleaseThread(TrackerThreadKind.Worker);
            }
        }

        LiveWorkers = 0;
        _recoveryAttempt = 0;
        Phase = QuarantinePhase.None;
        _publisher.PublishInactive();
        if (State == TrackerState.Disabled)
        {
            return;
        }

        State = TrackerState.Stopped;
        _guard.Release(this);
    }

    /// <summary>The root object reported Gone, <c>TaskbarCreated</c> arrived, or the shell process changed (04 F).</summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public void ShellGone()
    {
        if (State != TrackerState.Running)
        {
            return; // already recovering, quarantined (a trial recovers inside its engine), stopped, or disabled
        }

        State = TrackerState.Recovering;
        _recoveryAttempt = 0;
        _publisher.PublishInactive();
    }

    /// <summary>Delay before the next attempt to recreate <c>IShellWindows</c>: 250 ms, 500 ms, 1 s, 2 s, then every 5 s, unbounded.</summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public TimeSpan NextRecoveryDelay()
    {
        var d = _recoveryAttempt < RecoveryDelays.Length ? RecoveryDelays[_recoveryAttempt] : RecoverySteady;
        _recoveryAttempt++;
        return d;
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void RecoverySucceeded()
    {
        if (State == TrackerState.Recovering)
        {
            State = TrackerState.Running;
            _recoveryAttempt = 0;
        }
    }

    /// <summary>
    /// The watchdog abandoned the live worker (a normal or a trial worker). Invalidates the generation first (publishing
    /// Inactive), counts the abandoned thread and the wedge, records <paramref name="entry"/> in the process-wide record, and
    /// then: <c>ThreadCap</c> &gt; <c>CircuitBreaker</c> &gt; <c>Quarantined</c> (04 § M.6 Q1–Q3, Q10). <b>Never</b> a replacement.
    /// </summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public WedgeDecision OnWedge(QuarantineEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        bool trial = State == TrackerState.Quarantined && Phase == QuarantinePhase.Trial;
        if (!(State is TrackerState.Running or TrackerState.Recovering || trial) || LiveWorkers != 1)
        {
            throw new InvalidOperationException("A wedge can only be reported for the live worker of a running tracker or a trial.");
        }

        int generation = _publisher.Abandon();
        LiveWorkers = 0;
        int abandoned = _guard.AbandonThread(TrackerThreadKind.Worker);

        var now = _clock.GetTimestamp();
        Expire();
        _wedges.Enqueue(now);
        _guard.Quarantine.Add(entry);                      // recorded in every outcome (Q1, Q2 keep it for Retry)

        // The permanent reason wins when both limits are reached by the same wedge (04 B.6), so the UI can tell
        // "Retry may work" (CircuitBreaker) from "off until the app restarts" (ThreadCap).
        DisabledReason? reason = null;
        if (abandoned >= _limits.MaxAbandonedThreads)
        {
            reason = Explorer.DisabledReason.ThreadCap;
        }
        else if (_wedges.Count >= _limits.BreakerWedges)
        {
            reason = Explorer.DisabledReason.CircuitBreaker;
        }

        if (reason is { } r)
        {
            State = TrackerState.Disabled;
            DisabledReason = r;
            Phase = QuarantinePhase.None;
            return new WedgeDecision(WedgeOutcome.Disabled, generation, r);
        }

        State = TrackerState.Quarantined;
        Phase = QuarantinePhase.Waiting;
        return new WedgeDecision(WedgeOutcome.Quarantined, generation, Explorer.DisabledReason.None);
    }

    /// <summary>
    /// One look of the monitor (04 § M.6), with the record's assessment and the current count of full enumerations (trial
    /// evidence). Reserves the worker slot itself before it answers <see cref="QuarantineAction.StartWorker"/> or
    /// <see cref="QuarantineAction.StartTrial"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public QuarantineAction OnQuarantineTick(QuarantineAssessment assessment, long fullEnumerations)
    {
        if (State != TrackerState.Quarantined)
        {
            return QuarantineAction.None;                  // Disabled/Stopped: the record resolves silently (Q14), no restart
        }

        if (assessment.AllResolved)
        {
            if (Phase == QuarantinePhase.Trial)
            {
                EndQuarantine();                            // Q11: the trial worker becomes the normal worker
                return QuarantineAction.None;
            }

            if (!_guard.TryReserveThread(TrackerThreadKind.Worker))
            {
                return QuarantineAction.None;              // defensive: the slot is free while Waiting
            }

            EndQuarantine();                                // Q4
            LiveWorkers = 1;
            return QuarantineAction.StartWorker;
        }

        if (Phase == QuarantinePhase.Trial)
        {
            if (_clock.GetElapsedTime(_trialStartedAt) < _guard.Quarantine.Options.TrialDeadline)
            {
                return QuarantineAction.None;
            }

            if (fullEnumerations > _trialBaseline)
            {
                _guard.Quarantine.ClearByTrial();           // Q8: passed; the record is cleared only now
                EndQuarantine();
                return QuarantineAction.None;
            }

            return QuarantineAction.StopTrial;             // Q9: inconclusive
        }

        if (assessment.HoldComplete && assessment.AutomaticTrialAllowed && TryStartTrial(TrialKind.Automatic, fullEnumerations))
        {
            return QuarantineAction.StartTrial;            // Q5
        }

        return QuarantineAction.None;                      // Q6, Q7
    }

    /// <summary>
    /// The host stopped an inconclusive trial worker and it ended in time (Q9). Its generation is invalidated (Inactive) and the
    /// state returns to Waiting; another complete hold is needed. A trial that did not end is reported through <see cref="OnWedge"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public void TrialStopped()
    {
        if (State != TrackerState.Quarantined || Phase != QuarantinePhase.Trial || LiveWorkers != 1)
        {
            throw new InvalidOperationException("No trial worker is running.");
        }

        _publisher.Abandon();
        _guard.ReleaseThread(TrackerThreadKind.Worker);
        LiveWorkers = 0;
        Phase = QuarantinePhase.Waiting;
        _guard.Quarantine.RestartHold();
    }

    /// <summary>
    /// The user's explicit Retry (Settings, T3.4), 04 § M.6 Q19–Q24. <paramref name="hangEvidence"/> is the record's hang evidence
    /// right now; <paramref name="fullEnumerations"/> the trial baseline. Never clears the record, never resets the cap.
    /// </summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public RetryResult Retry(bool hangEvidence = false, long fullEnumerations = 0)
    {
        if (State == TrackerState.Quarantined)
        {
            if (Phase == QuarantinePhase.Trial)
            {
                return RetryResult.TrialInProgress;        // Q20: one trial at a time
            }

            if (_guard.AbandonedThreads >= _limits.MaxAbandonedThreads)
            {
                return RetryResult.ThreadCapReached;
            }

            if (hangEvidence)
            {
                return RetryResult.ServerNotResponding;    // Q21
            }

            return TryStartTrial(TrialKind.Explicit, fullEnumerations) ? RetryResult.TrialStarted : RetryResult.ThreadCapReached;
        }

        if (State != TrackerState.Disabled)
        {
            return RetryResult.NotDisabled;                // Q19
        }

        if (_guard.AbandonedThreads >= _limits.MaxAbandonedThreads)
        {
            return RetryResult.ThreadCapReached;           // Q24 (and a breaker trip that also used up the cap)
        }

        if (_guard.Quarantine.HasUnresolved)
        {
            if (hangEvidence)
            {
                return RetryResult.ServerNotResponding;    // Q22: the breaker window is kept
            }

            // Q22: the breaker window is cleared (Q1 rule) and the retry is an explicit trial against the unresolved server.
            _wedges.Clear();
            DisabledReason = Explorer.DisabledReason.None;
            State = TrackerState.Quarantined;
            Phase = QuarantinePhase.Waiting;
            if (TryStartTrial(TrialKind.Explicit, fullEnumerations))
            {
                return RetryResult.TrialStarted;
            }

            return RetryResult.ThreadCapReached;           // defensive: cannot happen below the cap with the slot free
        }

        if (!_guard.TryReserveThread(TrackerThreadKind.Worker))
        {
            return RetryResult.ThreadCapReached; // defensive; cannot happen while the slot is free
        }

        // Q23. The lifetime abandoned count is deliberately NOT touched: Retry buys one more cycle, not new budget.
        _wedges.Clear();
        DisabledReason = Explorer.DisabledReason.None;
        State = TrackerState.Running;
        LiveWorkers = 1;
        return RetryResult.Started;
    }

    /// <summary>
    /// True when the tracker is off for the rest of this process (reason <see cref="DisabledReason.ThreadCap"/>):
    /// Settings must say so plainly and offer no Retry (04 B.6).
    /// </summary>
    public bool IsPermanentlyDisabled => State == TrackerState.Disabled && DisabledReason == DisabledReason.ThreadCap;

    /// <summary>True when Settings should offer Retry: paused by the breaker, or quarantined with no trial running, below the cap.</summary>
    public bool CanRetry =>
        _guard.AbandonedThreads < _limits.MaxAbandonedThreads
        && (State == TrackerState.Disabled || (State == TrackerState.Quarantined && Phase == QuarantinePhase.Waiting));

    /// <summary>Called under the lock: Waiting → Trial with the one worker slot reserved and every suspect marked tried (04 § M.5).</summary>
    private bool TryStartTrial(TrialKind kind, long fullEnumerations)
    {
        if (State != TrackerState.Quarantined || Phase != QuarantinePhase.Waiting || LiveWorkers != 0
            || _guard.AbandonedThreads >= _limits.MaxAbandonedThreads
            || !_guard.TryReserveThread(TrackerThreadKind.Worker))
        {
            return false;
        }

        _guard.Quarantine.MarkTried();                     // starting a trial never clears the record
        Phase = QuarantinePhase.Trial;
        TrialKind = kind;
        LiveWorkers = 1;
        _trialStartedAt = _clock.GetTimestamp();
        _trialBaseline = fullEnumerations;
        return true;
    }

    private void EndQuarantine()
    {
        State = TrackerState.Running;
        Phase = QuarantinePhase.None;
        _recoveryAttempt = 0;
    }

    private void Expire()
    {
        var now = _clock.GetTimestamp();
        while (_wedges.Count > 0 && _clock.GetElapsedTime(_wedges.Peek(), now) >= _limits.BreakerWindow)
        {
            _wedges.Dequeue();
        }
    }
}

internal enum RootVerdict
{
    Ok,
    Suspect,

    /// <summary>Release the root and recreate it (04 F: a private empty ShellWindows server, L7).</summary>
    ReplaceRoot,
}

/// <summary>
/// 04 F: after (re)creating <c>IShellWindows</c>, if Explorer frames exist but two rebuilds in a row enumerate
/// nothing, the root is a private empty server and must be replaced.
/// </summary>
internal sealed class RootValidator
{
    private int _emptyWhileFramesExist;

    public RootVerdict Observe(int enumeratedTabs, bool explorerFramesExist)
    {
        if (enumeratedTabs > 0 || !explorerFramesExist)
        {
            _emptyWhileFramesExist = 0;
            return RootVerdict.Ok;
        }

        if (++_emptyWhileFramesExist >= 2)
        {
            _emptyWhileFramesExist = 0;
            return RootVerdict.ReplaceRoot;
        }

        return RootVerdict.Suspect;
    }

    public void Reset() => _emptyWhileFramesExist = 0;
}
