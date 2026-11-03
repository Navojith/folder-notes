using System.Runtime.CompilerServices;

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

    public TrackerProcessGuard(TrackerThreadBudget? budget = null) => _budget = budget ?? TrackerThreadBudget.Default;

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
    // Thread safety: the worker thread (recovery), the watchdog thread (OnWedge) and the UI thread (Start/Stop/Retry) all call
    // this. Every mutating method is [Synchronized] (locks this; the machine is never exposed outside the tracker), and the
    // state properties are single enum/int reads, atomic on x64. It calls StatePublisher (its own lock) but is never called back by it.
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
            lock (this)
            {
                Expire();
                return _wedges.Count;
            }
        }
    }

    /// <summary>Stopped → Running. The host then starts the one worker.</summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public StartResult Start()
    {
        switch (State)
        {
            case TrackerState.Disabled:
                return StartResult.Disabled;
            case TrackerState.Running or TrackerState.Recovering:
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
    /// Any state → Stopped (a Disabled tracker stays Disabled and keeps the process slot: the latch is not an
    /// escape hatch, and no second tracker may start in its place), publishes Inactive, and frees the process slot otherwise. The host ends the worker (bounded wait).
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
            return; // already recovering, stopped, or disabled
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
    /// The watchdog abandoned the worker. Invalidates the generation first (publishing Inactive), counts the
    /// abandoned thread, then either allows exactly one replacement or disables the tracker.
    /// </summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public WedgeDecision OnWedge()
    {
        if (State is not (TrackerState.Running or TrackerState.Recovering) || LiveWorkers != 1)
        {
            throw new InvalidOperationException("A wedge can only be reported for the live worker of a running tracker.");
        }

        int generation = _publisher.Abandon();
        LiveWorkers = 0;
        int abandoned = _guard.AbandonThread(TrackerThreadKind.Worker);

        var now = _clock.GetTimestamp();
        Expire();
        _wedges.Enqueue(now);

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
            return new WedgeDecision(WedgeOutcome.Disabled, generation, r);
        }

        return new WedgeDecision(WedgeOutcome.StartReplacement, generation, Explorer.DisabledReason.None);
    }

    /// <summary>The host started the replacement worker. Refused if one is already live (thread limit).</summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
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

        if (!_guard.TryReserveThread(TrackerThreadKind.Worker))
        {
            throw new InvalidOperationException("The process thread budget refuses another live worker.");
        }

        LiveWorkers = 1;
    }

    /// <summary>The user's explicit Retry (Settings, T3.4): the only way out of Disabled, and only below the thread cap.</summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
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

        if (!_guard.TryReserveThread(TrackerThreadKind.Worker))
        {
            return RetryResult.ThreadCapReached; // defensive; cannot happen while the slot is free
        }

        // The lifetime abandoned count is deliberately NOT touched: Retry buys one more cycle, not new budget.
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

    /// <summary>True when the Retry action would be accepted right now (what Settings uses to show the button).</summary>
    public bool CanRetry => State == TrackerState.Disabled && _guard.AbandonedThreads < _limits.MaxAbandonedThreads;

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
