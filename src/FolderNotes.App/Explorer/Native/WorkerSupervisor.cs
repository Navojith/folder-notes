namespace FolderNotes.App.Explorer.Native;

/// <summary>
/// Owns the lifetime of the COM worker (04 B.6): start it, replace it when the watchdog declares it wedged, stop it
/// within a bound, and honour the breaker and the process-wide cap through <see cref="TrackerStateMachine"/>. The
/// hooks are not touched here; they live on the hook thread (04 B.7), so a replacement is
/// <c>Detach(old)</c> → <c>OnWedge</c> → <c>Attach(new)</c> and nothing else.
/// <para>
/// Order of events for a wedge (all under one lock, so a Stop cannot interleave): the old worker is detached from the
/// router and un-watched; the machine bumps the publisher generation and publishes Inactive (an abandoned worker can no
/// longer publish); then, if allowed, exactly one replacement is created, started within a bound, attached and watched.
/// A replacement that does not even start counts as another wedge, so the breaker and the cap bound this loop.
/// </para>
/// </summary>
internal sealed class WorkerSupervisor : IDisposable
{
    private readonly TrackerStateMachine _machine;
    private readonly SignalRouter _router;
    private readonly StatePublisher _publisher;
    private readonly WatchdogThread _watchdog;
    private readonly Func<int, StaWorkerHost> _createWorker;
    private readonly Action<string> _log;
    private readonly TimeSpan _startTimeout;
    private readonly TimeSpan _stopTimeout;
    private readonly Lock _gate = new();
    private StaWorkerHost? _current;
    private int _replacements;

    public WorkerSupervisor(
        TrackerStateMachine machine,
        SignalRouter router,
        StatePublisher publisher,
        WatchdogThread watchdog,
        Func<int, StaWorkerHost> createWorker,
        Action<string>? log = null,
        TimeSpan? startTimeout = null,
        TimeSpan? stopTimeout = null)
    {
        _machine = machine ?? throw new ArgumentNullException(nameof(machine));
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _watchdog = watchdog ?? throw new ArgumentNullException(nameof(watchdog));
        _createWorker = createWorker ?? throw new ArgumentNullException(nameof(createWorker));
        _log = log ?? (_ => { });
        _startTimeout = startTimeout ?? TimeSpan.FromSeconds(1);
        _stopTimeout = stopTimeout ?? TimeSpan.FromSeconds(2);
    }

    /// <summary>The live worker, or null (stopped, disabled, between replacements).</summary>
    public StaWorkerHost? Current
    {
        get { lock (_gate) { return _current; } }
    }

    /// <summary>Replacement workers started so far (diagnostics, <c>TrackerStats</c>).</summary>
    public int Replacements => Volatile.Read(ref _replacements);

    public TrackerStateMachine Machine => _machine;

    /// <summary>Start the watchdog and the first worker. Refused (not started) when disabled, when another tracker is active, or when the budget says no.</summary>
    public StartResult Start()
    {
        lock (_gate)
        {
            var result = _machine.Start();
            if (result != StartResult.Started)
            {
                return result;
            }

            if (!_watchdog.IsRunning && !_watchdog.Start())
            {
                _machine.Stop();
                return StartResult.AnotherTrackerActive;   // the budget refused a second watchdog
            }

            BringUpWorkerLocked(_publisher.Generation);
            return StartResult.Started;
        }
    }

    /// <summary>The user's explicit Retry: leaves Disabled (only below the cap) and brings up one worker.</summary>
    public RetryResult Retry()
    {
        lock (_gate)
        {
            var result = _machine.Retry();
            if (result == RetryResult.Started)
            {
                if (!_watchdog.IsRunning)
                {
                    _watchdog.Start();
                }

                BringUpWorkerLocked(_publisher.Generation);
            }

            return result;
        }
    }

    /// <summary>
    /// Stop in the order of 04 B.7: detach from the router, un-watch, end the worker (≤ stop timeout, then abandon),
    /// stop the watchdog. Returns true if everything ended in time. Never blocks longer than the bounds, even while wedged.
    /// </summary>
    public bool Stop()
    {
        StaWorkerHost? worker;
        lock (_gate)
        {
            worker = _current;
            _current = null;
            if (worker is not null)
            {
                _router.Detach(worker);
                _watchdog.Unwatch(worker);
            }
        }

        bool clean = worker?.Stop(_stopTimeout) ?? true;
        lock (_gate)
        {
            _machine.Stop(workerAbandoned: !clean);
        }

        bool watchdogClean = _watchdog.Stop(TimeSpan.FromSeconds(1));
        return clean && watchdogClean;
    }

    public void Dispose() => Stop();

    /// <summary>Called on the watchdog thread when a worker did not return after the cancel (04 B.6).</summary>
    public void OnWorkerWedged(IWatchedWorker watched)
    {
        lock (_gate)
        {
            if (watched is not StaWorkerHost worker || !ReferenceEquals(_current, worker))
            {
                return;                                    // already stopped or replaced: nothing to do
            }

            _log($"Explorer tracker worker did not respond (generation {worker.Generation}); replacing it.");
            AbandonLocked(worker);
        }
    }

    private void AbandonLocked(StaWorkerHost worker)
    {
        _router.Detach(worker);                            // it gets no more wake-ups and takes no bits
        _watchdog.Unwatch(worker);
        worker.MarkAbandoned();                            // asks its loop to quit if it ever returns; it is never joined
        _current = null;

        var decision = _machine.OnWedge();                 // bumps the generation and publishes Inactive FIRST
        if (decision.Outcome == WedgeOutcome.StartReplacement)
        {
            _machine.ReplacementStarted();
            Interlocked.Increment(ref _replacements);
            BringUpStartedWorkerLocked(decision.Generation);
        }
        else
        {
            _log($"Explorer tracking disabled: {decision.Reason}.");
        }
    }

    /// <summary>Machine says one worker is allowed (Start/Retry already counted it): create and start it.</summary>
    private void BringUpWorkerLocked(int generation) => BringUpStartedWorkerLocked(generation);

    private void BringUpStartedWorkerLocked(int generation)
    {
        while (true)
        {
            var worker = _createWorker(generation);
            if (worker.Start(_startTimeout))
            {
                _current = worker;
                _watchdog.Watch(worker);
                _router.Attach(worker);                    // bits raised while there was no worker wake it now
                return;
            }

            // The thread could not even start: it counts as a wedge, so the breaker and the cap end this loop.
            _log("Explorer tracker worker did not start in time.");
            worker.MarkAbandoned();
            var decision = _machine.OnWedge();
            if (decision.Outcome != WedgeOutcome.StartReplacement)
            {
                _log($"Explorer tracking disabled: {decision.Reason}.");
                return;
            }

            _machine.ReplacementStarted();
            Interlocked.Increment(ref _replacements);
            generation = decision.Generation;
        }
    }
}
