namespace FolderNotes.App.Explorer.Native;

/// <summary>
/// Owns the lifetime of the COM worker (04 B.6, § M): start it, abandon it when the watchdog declares it wedged, run the
/// quarantine monitor, start trial workers, stop within a bound, and honour the breaker, the cap and the quarantine record
/// through <see cref="TrackerStateMachine"/>. The hooks are not touched here; they live on the hook thread (04 B.7).
/// <para>
/// A wedge (all under one lock, so a Stop cannot interleave): the old worker is detached from the router and un-watched; its
/// journal is read into a quarantine entry; the machine bumps the publisher generation, publishes Inactive and decides
/// Quarantined or Disabled. <b>No replacement is started</b> (04 § M.1 rule 4): a new worker starts only when the monitor finds
/// evidence (E1/E2), grants a trial after a complete health hold, or the user's Retry is accepted. A worker that does not even
/// start counts as a wedge too, and nothing loops.
/// </para>
/// </summary>
internal sealed class WorkerSupervisor : IDisposable
{
    private readonly TrackerStateMachine _machine;
    private readonly SignalRouter _router;
    private readonly StatePublisher _publisher;
    private readonly WatchdogThread _watchdog;
    private readonly Func<int, StaWorkerHost> _createWorker;
    private readonly IServerProbe _probe;
    private readonly Func<long> _fullEnumerations;
    private readonly Action<string> _log;
    private readonly TimeSpan _startTimeout;
    private readonly TimeSpan _stopTimeout;
    private readonly Action? _beforeWorker;
    private readonly Action? _onStopping;
    private readonly Lock _gate = new();
    private StaWorkerHost? _current;
    private int _replacements;
    private int _trialsStarted;
    private int _trialsWedged;
    private int _trialsInconclusive;
    private bool _stopping;
    private CallSite _lastWedgeSite;

    public WorkerSupervisor(
        TrackerStateMachine machine,
        SignalRouter router,
        StatePublisher publisher,
        WatchdogThread watchdog,
        Func<int, StaWorkerHost> createWorker,
        IServerProbe probe,
        Func<long> fullEnumerations,
        Action<string>? log = null,
        TimeSpan? startTimeout = null,
        TimeSpan? stopTimeout = null,
        Action? beforeWorker = null,
        Action? onStopping = null)
    {
        _beforeWorker = beforeWorker;
        _onStopping = onStopping;
        _machine = machine ?? throw new ArgumentNullException(nameof(machine));
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _watchdog = watchdog ?? throw new ArgumentNullException(nameof(watchdog));
        _createWorker = createWorker ?? throw new ArgumentNullException(nameof(createWorker));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _fullEnumerations = fullEnumerations ?? throw new ArgumentNullException(nameof(fullEnumerations));
        _log = log ?? (_ => { });
        _startTimeout = startTimeout ?? TimeSpan.FromSeconds(1);
        _stopTimeout = stopTimeout ?? TimeSpan.FromSeconds(2);
    }

    /// <summary>The live worker (normal or trial), or null (stopped, disabled, quarantined and waiting).</summary>
    public StaWorkerHost? Current
    {
        get { lock (_gate) { return _current; } }
    }

    /// <summary>Workers started after an abandonment: trials and resumed workers (diagnostics, <c>TrackerStats</c>).</summary>
    public int Replacements => Volatile.Read(ref _replacements);

    public (int Started, int Wedged, int Inconclusive) Trials => (Volatile.Read(ref _trialsStarted), Volatile.Read(ref _trialsWedged), Volatile.Read(ref _trialsInconclusive));

    public CallSite LastWedgeSite
    {
        get { lock (_gate) { return _lastWedgeSite; } }
    }

    public TrackerStateMachine Machine => _machine;

    /// <summary>
    /// Start the watchdog and the first worker, or only the watchdog (with its monitor) when a quarantine is in force (04 § M.6
    /// Q17). Refused (not started) when disabled, when another tracker is active, or when the budget says no.
    /// </summary>
    public StartResult Start()
    {
        lock (_gate)
        {
            if (_machine.Quarantine.HasUnresolved)
            {
                _machine.Quarantine.Assess(_probe);        // Q17: resolve what already resolved (E1/E2) before deciding
            }

            var result = _machine.Start();
            if (result is not (StartResult.Started or StartResult.Quarantined))
            {
                return result;
            }

            _stopping = false;
            SafeRun(_beforeWorker);                        // hooks first (04 B.7): the order is hook thread, watchdog, worker
            if (!_watchdog.IsRunning && !_watchdog.Start())
            {
                SafeRun(_onStopping);
                _machine.Stop();
                return StartResult.AnotherTrackerActive;   // the budget refused a second watchdog
            }

            if (result == StartResult.Started)
            {
                BringUpWorkerLocked(_publisher.Generation);
            }
            else
            {
                _log("Explorer tracking starts quarantined: an earlier unresponsive Explorer is unresolved (04 § M).");
                _watchdog.RequestMonitor();
            }

            return result;
        }
    }

    /// <summary>
    /// The user's explicit Retry (04 § M.6 Q19–Q24): leaves Disabled (only below the cap), or starts an explicit trial while
    /// quarantined, and only while no suspect shows hang evidence. Never clears the quarantine record.
    /// </summary>
    public RetryResult Retry()
    {
        lock (_gate)
        {
            var record = _machine.Quarantine;
            bool hang = record.HasUnresolved && record.HangEvidenceNow(_probe);
            var result = _machine.Retry(hang, _fullEnumerations());
            if (result is RetryResult.Started or RetryResult.TrialStarted)
            {
                _stopping = false;
                SafeRun(_beforeWorker);
                if (!_watchdog.IsRunning)
                {
                    _watchdog.Start();
                }

                if (result == RetryResult.TrialStarted)
                {
                    Interlocked.Increment(ref _trialsStarted);
                    Interlocked.Increment(ref _replacements);
                    _log("Explorer tracking: trial worker started by Retry; quarantine stays until it passes (04 § M.5).");
                }

                BringUpWorkerLocked(_publisher.Generation);
                _watchdog.RequestMonitor();                // the trial's deadline is watched by the monitor
            }

            return result;
        }
    }

    /// <summary>
    /// Stop in the order of 04 B.7: detach from the router, un-watch, end the worker (≤ stop timeout, then abandon),
    /// stop the watchdog (and with it the monitor). The quarantine record is kept (Q15/Q16). Never blocks longer than the bounds.
    /// </summary>
    public bool Stop()
    {
        StaWorkerHost? worker;
        lock (_gate)
        {
            _stopping = true;                              // the monitor starts nothing from now on
            worker = _current;
            _current = null;
            if (worker is not null)
            {
                _router.Detach(worker);
                _watchdog.Unwatch(worker);
            }
        }

        SafeRun(_onStopping);                              // hooks off after the worker is detached, before it ends (04 B.7)
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

            _log($"Explorer tracker worker did not respond (generation {worker.Generation}); abandoning it, no replacement (04 § M).");
            AbandonLocked(worker);
        }
    }

    /// <summary>
    /// The quarantine monitor (04 § M.4), run on the watchdog thread. Returns when to run again: the check interval while a
    /// quarantine or a trial is in force, null otherwise (the watchdog then waits on its event only).
    /// </summary>
    public TimeSpan? OnMonitorTick()
    {
        lock (_gate)
        {
            var record = _machine.Quarantine;
            if (_stopping || (_machine.State != TrackerState.Quarantined && !record.HasUnresolved))
            {
                return null;
            }

            var assessment = record.HasUnresolved ? record.Assess(_probe) : new QuarantineAssessment(true, false, false, false);
            switch (_machine.OnQuarantineTick(assessment, _fullEnumerations()))
            {
                case QuarantineAction.StartWorker:
                    _log("Explorer tracking resumes: the unresponsive operation completed or its server ended (04 § M.4).");
                    Interlocked.Increment(ref _replacements);
                    BringUpWorkerLocked(_publisher.Generation);
                    break;
                case QuarantineAction.StartTrial:
                    _log("Explorer tracking: health hold complete, starting one trial worker (04 § M.5).");
                    Interlocked.Increment(ref _trialsStarted);
                    Interlocked.Increment(ref _replacements);
                    BringUpWorkerLocked(_publisher.Generation);
                    break;
                case QuarantineAction.StopTrial:
                    StopTrialLocked();
                    break;
            }

            return _machine.State == TrackerState.Quarantined || record.HasUnresolved ? record.Options.CheckInterval : null;
        }
    }

    private void StopTrialLocked()
    {
        var worker = _current;
        _current = null;
        if (worker is null)
        {
            return;
        }

        _router.Detach(worker);
        _watchdog.Unwatch(worker);
        Interlocked.Increment(ref _trialsInconclusive);
        if (worker.Stop(_stopTimeout))
        {
            _machine.TrialStopped();
            _log("Explorer tracking: trial worker reached its deadline without a full enumeration; back to quarantine (04 § M.5).");
            return;
        }

        _log("Explorer tracking: trial worker did not stop in time; abandoned and counted (04 § M.5).");
        Wedge(worker);
    }

    private void SafeRun(Action? action)
    {
        try
        {
            action?.Invoke();
        }
        catch (Exception ex)
        {
            _log($"Supervisor callback failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void AbandonLocked(StaWorkerHost worker)
    {
        _router.Detach(worker);                            // it gets no more wake-ups and takes no bits
        _watchdog.Unwatch(worker);
        worker.MarkAbandoned();                            // asks its loop to quit if it ever returns; it is never joined
        _current = null;
        Wedge(worker);
    }

    /// <summary>Records the abandonment (journal → entry) and lets the machine decide. Never starts a worker.</summary>
    private void Wedge(StaWorkerHost worker)
    {
        bool wasTrial = _machine.State == TrackerState.Quarantined && _machine.Phase == QuarantinePhase.Trial;
        var entry = BuildEntry(worker);
        var decision = _machine.OnWedge(entry);            // bumps the generation and publishes Inactive FIRST
        if (wasTrial)
        {
            Interlocked.Increment(ref _trialsWedged);
        }

        if (decision.Outcome == WedgeOutcome.Disabled)
        {
            _log($"Explorer tracking disabled: {decision.Reason}.");
        }
        else
        {
            _log($"Explorer tracking quarantined: site {entry.Snapshot.Record.Site} ({entry.Snapshot.Kind}), suspect {entry.Suspect.Kind}"
                + $" {string.Join(",", entry.Suspect.Servers.Select(s => s.Pid))}, cancel 0x{entry.CancelHr ?? 0:X8} (04 § M).");
        }

        _watchdog.RequestMonitor();                        // the record changed: the monitor looks now and then every second
    }

    private QuarantineEntry BuildEntry(StaWorkerHost worker)
    {
        var snapshot = worker.Journal.Read(worker.Watchdog.StartedAt ?? 0);
        _lastWedgeSite = snapshot.IsActive ? snapshot.Record.Site : CallSite.None;
        Suspect suspect;
        try
        {
            suspect = Suspect.From(snapshot, _probe);
        }
        catch (Exception ex)
        {
            _log($"Quarantine: identifying the server failed ({ex.GetType().Name}); treating it as unknown.");
            suspect = Suspect.Unknown;
        }

        return new QuarantineEntry(worker, snapshot, suspect, worker.Watchdog.LastCancelHr, _machine.Quarantine.Clock.GetTimestamp());
    }

    /// <summary>The machine already reserved the slot (Start, Retry or the monitor): create and start the one worker.</summary>
    private void BringUpWorkerLocked(int generation)
    {
        var worker = _createWorker(generation);
        if (worker.Start(_startTimeout))
        {
            _current = worker;
            _watchdog.Watch(worker);
            _router.Attach(worker);                        // bits raised while there was no worker wake it now
            return;
        }

        // The thread could not even start (its first connection hung): a wedge like any other, and no retry loop.
        _log("Explorer tracker worker did not start in time.");
        worker.MarkAbandoned();
        Wedge(worker);
    }
}
