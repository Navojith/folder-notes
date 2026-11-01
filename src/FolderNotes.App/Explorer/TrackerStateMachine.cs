namespace FolderNotes.App.Explorer;

internal enum TrackerState
{
    Stopped,
    Running,

    /// <summary>The shell is gone or its root object is wrong: waiting to recreate it (04 F).</summary>
    Recovering,

    /// <summary>Latched off by the circuit breaker or the thread cap; only <see cref="TrackerStateMachine.Retry"/> leaves it.</summary>
    Disabled,
}

internal enum DisabledReason
{
    None,
    CircuitBreaker,
    ThreadCap,
}

internal enum StartResult
{
    Started,

    /// <summary>Another tracker is active in this process (04 B.6: at most one).</summary>
    AnotherTrackerActive,

    /// <summary>Disabled: only an explicit <see cref="TrackerStateMachine.Retry"/> restarts it.</summary>
    Disabled,

    /// <summary>Already running.</summary>
    AlreadyRunning,
}

internal enum RetryResult
{
    Started,
    NotDisabled,

    /// <summary>The lifetime thread cap is reached: stays disabled until the app restarts.</summary>
    ThreadCapReached,
}

internal enum WedgeOutcome
{
    /// <summary>Start exactly one replacement worker with the returned generation.</summary>
    StartReplacement,

    /// <summary>Do not start a worker; the machine is now Disabled.</summary>
    Disabled,
}

internal readonly record struct WedgeDecision(WedgeOutcome Outcome, int Generation, DisabledReason Reason);

internal sealed record TrackerLimits
{
    public static TrackerLimits Default { get; } = new();

    /// <summary>Wedges inside <see cref="BreakerWindow"/> that trip the breaker (04 B.6: 3).</summary>
    public int BreakerWedges { get; init; } = 3;

    /// <summary>A wedge counts while its age is below this (so after 10 quiet minutes it has expired).</summary>
    public TimeSpan BreakerWindow { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Most threads ever abandoned in this process (04 B.6: 3). NOTE: with the breaker also at 3 wedges, a trip
    /// uses up the whole cap, so <see cref="TrackerStateMachine.Retry"/> can never succeed after a trip. Raised
    /// as a specification question; see 09 (Q1).
    /// </summary>
    public int MaxAbandonedThreads { get; init; } = 3;
}

/// <summary>
/// Process-wide limits that must hold across tracker instances (04 B.6): one active tracker, and a lifetime cap
/// on abandoned threads. Production uses <see cref="Shared"/>; tests make their own.
/// </summary>
internal sealed class TrackerProcessGuard
{
    private readonly Lock _gate = new();
    private object? _owner;
    private int _abandoned;

    public static TrackerProcessGuard Shared { get; } = new();

    public int AbandonedThreads
    {
        get { lock (_gate) { return _abandoned; } }
    }

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

    public int RecordAbandoned()
    {
        lock (_gate)
        {
            return ++_abandoned;
        }
    }
}

/// <summary>
/// Lifecycle rules of 04 § ExplorerTracker design B.6 and F as a pure state machine: Stopped → Running ⇄ Recovering,
/// wedge → replacement or Disabled, the breaker, the thread limits and the recovery back-off. It never starts a
/// thread itself; it tells the host what is allowed.
/// <para>
/// Thread limits: <see cref="LiveWorkers"/> is 0 or 1 (a second worker is refused with an exception), the cap on
/// abandoned threads is process-wide, and <see cref="OnWedge"/> invalidates the publisher's generation (publishing
/// Inactive) <i>before</i> it returns a replacement, so an abandoned worker can never publish.
/// </para>
/// </summary>
internal sealed class TrackerStateMachine
{
    private static readonly TimeSpan[] RecoveryDelays =
        [TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)];

    private static readonly TimeSpan RecoverySteady = TimeSpan.FromSeconds(5);

    private readonly TimeProvider _clock;
    private readonly TrackerLimits _limits;
    private readonly TrackerProcessGuard _guard;
    private readonly StatePublisher _publisher;
    private readonly Queue<long> _wedges = new();
    private int _recoveryAttempt;

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

    /// <summary>Worker threads currently serving the tracker: 0 or 1.</summary>
    public int LiveWorkers { get; private set; }

    public int WedgesInWindow
    {
        get
        {
            Expire();
            return _wedges.Count;
        }
    }

    /// <summary>Stopped → Running. The host then starts the one worker.</summary>
    public StartResult Start()
    {
        switch (State)
        {
            case TrackerState.Disabled:
                return StartResult.Disabled;
            case TrackerState.Running or TrackerState.Recovering:
                return StartResult.AlreadyRunning;
        }

        if (!_guard.TryAcquire(this))
        {
            return StartResult.AnotherTrackerActive;
        }

        State = TrackerState.Running;
        LiveWorkers = 1;
        return StartResult.Started;
    }

    /// <summary>
    /// Any state → Stopped (a Disabled tracker stays Disabled and keeps the process slot: the latch is not an
    /// escape hatch, and no second tracker may start in its place), publishes Inactive, and frees the process slot otherwise. The host ends the worker (bounded wait).
    /// </summary>
    public void Stop()
    {
        LiveWorkers = 0;
        _recoveryAttempt = 0;
        _publisher.PublishInactive();
        if (State == TrackerState.Disabled)
        {
            return;
        }

        State = TrackerState.Stopped;
        _guard.Release(this);
    }

    /// <summary>The root object reported Gone, <c>TaskbarCreated</c> arrived, or the shell process changed (04 F).</summary>
    public void ShellGone()
    {
        if (State != TrackerState.Running)
        {
            return; // already recovering, stopped, or disabled
        }

        State = TrackerState.Recovering;
        _recoveryAttempt = 0;
        _publisher.PublishInactive();
    }

    /// <summary>Delay before the next attempt to recreate <c>IShellWindows</c>: 250 ms, 500 ms, 1 s, 2 s, then every 5 s, unbounded.</summary>
    public TimeSpan NextRecoveryDelay()
    {
        var d = _recoveryAttempt < RecoveryDelays.Length ? RecoveryDelays[_recoveryAttempt] : RecoverySteady;
        _recoveryAttempt++;
        return d;
    }

    public void RecoverySucceeded()
    {
        if (State == TrackerState.Recovering)
        {
            State = TrackerState.Running;
            _recoveryAttempt = 0;
        }
    }

    /// <summary>
    /// The watchdog abandoned the worker. Invalidates the generation first (publishing Inactive), counts the
    /// abandoned thread, then either allows exactly one replacement or disables the tracker.
    /// </summary>
    public WedgeDecision OnWedge()
    {
        if (State is not (TrackerState.Running or TrackerState.Recovering) || LiveWorkers != 1)
        {
            throw new InvalidOperationException("A wedge can only be reported for the live worker of a running tracker.");
        }

        int generation = _publisher.Abandon();
        LiveWorkers = 0;
        int abandoned = _guard.RecordAbandoned();

        var now = _clock.GetTimestamp();
        Expire();
        _wedges.Enqueue(now);

        DisabledReason? reason = null;
        if (_wedges.Count >= _limits.BreakerWedges)
        {
            reason = Explorer.DisabledReason.CircuitBreaker;
        }
        else if (abandoned >= _limits.MaxAbandonedThreads)
        {
            reason = Explorer.DisabledReason.ThreadCap;
        }

        if (reason is { } r)
        {
            State = TrackerState.Disabled;
            DisabledReason = r;
            return new WedgeDecision(WedgeOutcome.Disabled, generation, r);
        }

        return new WedgeDecision(WedgeOutcome.StartReplacement, generation, Explorer.DisabledReason.None);
    }

    /// <summary>The host started the replacement worker. Refused if one is already live (thread limit).</summary>
    public void ReplacementStarted()
    {
        if (State is not (TrackerState.Running or TrackerState.Recovering))
        {
            throw new InvalidOperationException("No replacement is allowed in this state.");
        }

        if (LiveWorkers != 0)
        {
            throw new InvalidOperationException("A worker is already live; at most one is allowed.");
        }

        LiveWorkers = 1;
    }

    /// <summary>The user's explicit Retry (Settings, T3.4): the only way out of Disabled, and only below the thread cap.</summary>
    public RetryResult Retry()
    {
        if (State != TrackerState.Disabled)
        {
            return RetryResult.NotDisabled;
        }

        if (_guard.AbandonedThreads >= _limits.MaxAbandonedThreads)
        {
            return RetryResult.ThreadCapReached;
        }

        _wedges.Clear();
        DisabledReason = Explorer.DisabledReason.None;
        State = TrackerState.Running;
        LiveWorkers = 1;
        return RetryResult.Started;
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
