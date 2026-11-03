using FolderNotes.App.Explorer;
using FolderNotes.App.Explorer.Native;

namespace FolderNotes.App.Tests.Explorer;

/// <summary>
/// 04 B.2, B.6, B.7 and § M with real threads: the STA worker host, the watchdog thread (and its quarantine monitor) and the
/// supervisor (cancel → abandon → quarantine → evidence or trial → breaker → cap). Time limits are scaled down (60 ms cancel,
/// 120 ms abandon, 50 ms monitor interval) so a wedge takes ~200 ms.
/// </summary>
[Trait("Kind", "Timing")]
public sealed class WorkerSupervisorTests : IDisposable
{
    private static readonly WatchdogOptions Quick = new() { CancelAfter = TimeSpan.FromMilliseconds(60), AbandonAfterCancel = TimeSpan.FromMilliseconds(120) };

    /// <summary>Monitor every 50 ms; hold and minimum long enough that no automatic trial happens unless a test shortens them.</summary>
    private static readonly QuarantineOptions NoAutoTrial = new() { CheckInterval = TimeSpan.FromMilliseconds(50) };

    private readonly SignalRouter _router = new();
    private readonly List<ActiveFolderState> _delivered = [];
    private readonly StatePublisher _publisher;
    private readonly FakeServerProbe _probe = new();
    private readonly ManualResetEventSlim _block = new(false);   // releasing it frees every "stuck" worker at the end
    private readonly List<uint> _cancelled = [];
    private readonly List<(int Generation, int Thread, TrackerSignal Bits)> _wakes = [];
    private readonly List<string> _log = [];
    private readonly Lock _lock = new();
    private TrackerProcessGuard _guard = null!;
    private TrackerStateMachine _machine = null!;
    private WatchdogThread _watchdog = null!;
    private WorkerSupervisor _supervisor = null!;
    private int _workersCreated;
    private int _blockNextWakes;                              // how many wake-ups will hang
    private int _failNextStarts;                              // how many workers hang in onStart
    private int _throwNextWakes;
    private int _cancelDelayMs;                               // makes CoCancelCall "slow" to exercise the instrumentation
    private int _cancelHr;                                    // what the fake CoCancelCall returns
    private long _fullEnumerations;                           // trial evidence (04 § M.5); a test bumps it
    private int _enumerateOnStart;                            // when 1, every worker "enumerates" in onStart
    private readonly List<(int Generation, int Thread, ApartmentState Apartment)> _starts = [];
    private readonly List<(int Generation, int Thread)> _stops = [];

    public WorkerSupervisorTests()
    {
        _publisher = new StatePublisher(_delivered.Add);
        _probe.Run(4242);                                      // the Explorer process the stuck "call" targets
        Reset(NoAutoTrial);
    }

    private void Reset(QuarantineOptions options)
    {
        _guard = new TrackerProcessGuard(quarantine: new QuarantineRecord(TimeProvider.System, options));
        _machine = new TrackerStateMachine(TimeProvider.System, _publisher, _guard);
        Build(startTimeout: TimeSpan.FromMilliseconds(300), stopTimeout: TimeSpan.FromMilliseconds(400));
    }

    private void Build(TimeSpan startTimeout, TimeSpan stopTimeout)
    {
        _watchdog = new WatchdogThread(
            _guard,
            w => _supervisor.OnWorkerWedged(w),
            tid => { if (_cancelDelayMs > 0) { Thread.Sleep(_cancelDelayMs); } lock (_lock) { _cancelled.Add(tid); } return _cancelHr; },
            m => _log.Add(m),
            () => _supervisor.OnMonitorTick());
        _supervisor = new WorkerSupervisor(_machine, _router, _publisher, _watchdog, CreateWorker, _probe, () => Interlocked.Read(ref _fullEnumerations), m => _log.Add(m), startTimeout, stopTimeout);
    }

    private StaWorkerHost CreateWorker(int generation)
    {
        Interlocked.Increment(ref _workersCreated);
        StaWorkerHost? self = null;
        self = new StaWorkerHost(
            generation,
            onStart: h =>
            {
                lock (_lock) { _starts.Add((generation, Environment.CurrentManagedThreadId, Thread.CurrentThread.GetApartmentState())); }
                if (Interlocked.Decrement(ref _failNextStarts) >= 0)
                {
                    _block.Wait();                             // never finishes starting
                }

                if (Volatile.Read(ref _enumerateOnStart) == 1)
                {
                    Interlocked.Increment(ref _fullEnumerations);   // as the engine does after a validated connect
                }
            },
            onWake: h =>
            {
                var bits = _router.Consume(h);
                lock (_lock) { _wakes.Add((generation, Environment.CurrentManagedThreadId, bits)); }
                if (Interlocked.Decrement(ref _throwNextWakes) >= 0)
                {
                    throw new InvalidOperationException("boom");
                }

                if (Interlocked.Decrement(ref _blockNextWakes) >= 0)
                {
                    using var op = h.Journal.Scope(CallSite.PathRead, 4242);   // the "call" targets a known server
                    _block.Wait();                             // a non-COM wait: CoCancelCall cannot help, only abandonment can
                }
            },
            onStop: h =>
            {
                lock (_lock) { _stops.Add((generation, Environment.CurrentManagedThreadId)); }
            },
            watchdogOptions: Quick,
            log: m => _log.Add(m),
            afterBegin: () => _watchdog.Poke());
        return self;
    }

    public void Dispose()
    {
        _block.Set();                                          // free zombies so the test run leaves no stuck thread
        _supervisor.Stop();
        _block.Dispose();
    }

    private static void WaitFor(Func<bool> condition, string what, int ms = 5000) =>
        SpinWait.SpinUntil(condition, TimeSpan.FromMilliseconds(ms)).ShouldBeTrue($"timed out waiting for: {what}");

    private int WakesBy(int generation)
    {
        lock (_lock) { return _wakes.Count(w => w.Generation == generation); }
    }

    // ---- the STA host --------------------------------------------------------------------------------

    [Fact]
    public void NFR01_The_worker_is_an_STA_thread_and_every_delegate_runs_on_it()
    {
        _supervisor.Start().ShouldBe(StartResult.Started);
        var w = _supervisor.Current!;
        _router.Raise(TrackerSignal.NameChange);
        WaitFor(() => WakesBy(0) == 1, "first wake");
        _supervisor.Stop().ShouldBeTrue();

        _starts.Single().Apartment.ShouldBe(ApartmentState.STA);
        _starts.Single().Thread.ShouldBe(w.ManagedThreadId);
        _wakes.Single().Thread.ShouldBe(w.ManagedThreadId);
        _stops.Single().Thread.ShouldBe(w.ManagedThreadId);    // COM objects are released where they were made
        _wakes.Single().Bits.ShouldBe(TrackerSignal.NameChange);
        w.ManagedThreadId.ShouldNotBe(Environment.CurrentManagedThreadId);
    }

    [Fact]
    public void NFR01_A_throwing_delegate_never_kills_the_loop_or_counts_as_a_wedge()
    {
        _supervisor.Start();
        _throwNextWakes = 1;

        _router.Raise(TrackerSignal.Foreground);
        WaitFor(() => WakesBy(0) == 1, "throwing wake");
        _router.Raise(TrackerSignal.ShowHide);
        WaitFor(() => WakesBy(0) == 2, "wake after the exception");

        _log.ShouldContain(m => m.Contains("evaluation failed", StringComparison.OrdinalIgnoreCase));
        _supervisor.Replacements.ShouldBe(0);
        _guard.AbandonedThreads.ShouldBe(0);
    }

    [Fact]
    public void NFR01_Posted_actions_run_in_order_on_the_worker()
    {
        _supervisor.Start();
        var w = _supervisor.Current!;
        var order = new List<int>();
        for (int i = 0; i < 20; i++)
        {
            int n = i;
            w.Post(() => order.Add(n)).ShouldBeTrue();
        }

        WaitFor(() => order.Count == 20, "all posted actions");
        order.ShouldBe(Enumerable.Range(0, 20));
    }

    // ---- idle ---------------------------------------------------------------------------------------

    [Fact]
    public void NFR06_An_idle_worker_causes_no_cancel_no_wedge_and_no_replacement()
    {
        _supervisor.Start();

        Thread.Sleep(500);                                     // several times the abandon limit, with no evaluation running

        _watchdog.CancelCalls.ShouldBe(0);
        _supervisor.Replacements.ShouldBe(0);
        _guard.AbandonedThreads.ShouldBe(0);
        _supervisor.Current.ShouldNotBeNull();
    }

    // ---- cancel, abandon, quarantine (04 § M) -------------------------------------------------------

    private void WaitQuarantined(int abandoned) =>
        WaitFor(() => _guard.AbandonedThreads == abandoned && _supervisor.Current is null && _machine.State is TrackerState.Quarantined or TrackerState.Disabled, $"abandonment {abandoned} handled");

    [Fact]
    public void NFR01_M_A_stuck_evaluation_is_cancelled_abandoned_and_quarantined_with_no_replacement_while_the_block_lasts()
    {
        _supervisor.Start();
        var old = _supervisor.Current!;
        _publisher.TryPublish(_publisher.Generation, _publisher.NextSequence(), new ActiveFolderState(1, 1, @"C:\a", 0));
        int genBefore = _publisher.Generation;
        _blockNextWakes = 1;

        _router.Raise(TrackerSignal.NameChange);               // the worker hangs inside this evaluation
        WaitQuarantined(1);
        Thread.Sleep(600);                                     // a dozen monitor looks: still nothing may start

        _watchdog.CancelCalls.ShouldBe(1);                     // cancel came first, once, aimed at the stuck thread
        _cancelled.ShouldBe([old.ThreadId]);
        old.IsAbandoned.ShouldBeTrue();
        _publisher.Generation.ShouldBe(genBefore + 1);
        _delivered[^1].IsInactive.ShouldBeTrue();              // the panel was told "nothing" when the worker was abandoned
        _machine.State.ShouldBe(TrackerState.Quarantined);
        _machine.Phase.ShouldBe(QuarantinePhase.Waiting);
        _supervisor.Current.ShouldBeNull();
        _supervisor.Replacements.ShouldBe(0);
        _workersCreated.ShouldBe(1);
        _guard.LiveThreads(TrackerThreadKind.Worker).ShouldBe(0);
        var entry = _guard.Quarantine.Entries.Single();
        entry.Snapshot.Record.Site.ShouldBe(CallSite.PathRead);   // the journal named the blocked operation...
        entry.Suspect.Kind.ShouldBe(SuspectKind.Known);           // ...and its server
        entry.Suspect.Servers.Single().Pid.ShouldBe(4242u);
    }

    [Fact]
    public void NFR01_M4_E1_When_the_blocked_call_returns_one_worker_resumes_within_the_bound()
    {
        _supervisor.Start();
        _blockNextWakes = 1;
        _router.Raise(TrackerSignal.NameChange);
        WaitQuarantined(1);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        _block.Set();                                          // the server finally answers: the zombie's operation completes
        WaitFor(() => _machine.State == TrackerState.Running && _supervisor.Current is not null, "resumed after E1", 1500);

        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromMilliseconds(1500));
        _guard.Quarantine.HasUnresolved.ShouldBeFalse();
        _guard.Quarantine.Releases.ByCompletion.ShouldBe(1);
        _guard.AbandonedThreads.ShouldBe(1);                   // the zombie's return never refunds the cap
        _router.Raise(TrackerSignal.ShowHide);
        WaitFor(() => WakesBy(_publisher.Generation) == 1, "the new worker serves the next signal");
    }

    [Fact]
    public void NFR01_M4_E2_When_the_suspect_server_exits_one_worker_resumes()
    {
        _probe.Run(4242);
        _supervisor.Start();
        _blockNextWakes = 1;
        _router.Raise(TrackerSignal.NameChange);
        WaitQuarantined(1);

        _probe.Exit(4242);                                     // the frozen Explorer process ended (or crashed)

        WaitFor(() => _machine.State == TrackerState.Running && _supervisor.Current is not null, "resumed after E2", 1500);
        _guard.Quarantine.Releases.ByServerEnd.ShouldBe(1);
    }

    [Fact]
    public void NFR01_M1_A_successful_cancel_request_is_kept_but_never_releases_the_quarantine()
    {
        _cancelHr = 0;                                         // S_OK: "an attempt was made"
        _supervisor.Start();
        _blockNextWakes = 1;
        _router.Raise(TrackerSignal.NameChange);
        WaitQuarantined(1);

        Thread.Sleep(400);

        _guard.Quarantine.Entries.Single().CancelHr.ShouldBe(0);
        _watchdog.LastCancelHr.ShouldBe(0);
        _machine.State.ShouldBe(TrackerState.Quarantined);     // still quarantined: S_OK proves nothing
        _supervisor.Current.ShouldBeNull();
    }

    [Fact]
    public void NFR01_M1_The_cancel_HRESULT_is_recorded_per_worker()
    {
        _cancelHr = unchecked((int)0x8001011E);                // "cancel not possible": not in a cancellable COM wait
        _supervisor.Start();
        var old = _supervisor.Current!;
        _blockNextWakes = 1;
        _router.Raise(TrackerSignal.NameChange);
        WaitQuarantined(1);

        old.Watchdog.LastCancelHr.ShouldBe(unchecked((int)0x8001011E));
        _guard.Quarantine.Entries.Single().CancelHr.ShouldBe(unchecked((int)0x8001011E));
    }

    [Fact]
    public void NFR01_An_abandoned_worker_cannot_publish_and_takes_no_signals_even_after_it_returns()
    {
        _supervisor.Start();
        var old = _supervisor.Current!;
        int oldGen = _publisher.Generation;
        _blockNextWakes = 1;
        _router.Raise(TrackerSignal.NameChange);
        WaitQuarantined(1);

        _publisher.TryPublish(oldGen, _publisher.NextSequence(), new ActiveFolderState(9, 9, @"C:\zombie", 0))
            .ShouldBe(PublishResult.StaleGeneration);          // the generation gate (04 B.6 (3))
        _router.Consume(old).ShouldBe(TrackerSignal.None);

        _block.Set();                                          // the zombie finally returns: it must go away quietly
        WaitFor(() => old.HasExited, "zombie exit");
        _delivered.ShouldNotContain(s => s.Path == @"C:\zombie");
        _stops.ShouldContain(s => s.Generation == oldGen);     // and it released its own objects on its own thread
    }

    [Fact]
    public void NFR01_Replacing_a_worker_leaves_the_hook_layer_alone()
    {
        // The supervisor has no reference to the hook thread at all (04 B.7): a new worker only re-points the router.
        typeof(WorkerSupervisor).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Select(f => f.FieldType).ShouldNotContain(typeof(HookThread));
        typeof(StaWorkerHost).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Select(f => f.FieldType).ShouldNotContain(typeof(HookThread));
    }

    /// <summary>Wedge the live worker and wait until the supervisor has handled it.</summary>
    private void WedgeOnce()
    {
        int abandonedBefore = _guard.AbandonedThreads;
        _blockNextWakes = 1;
        _router.Raise(TrackerSignal.NameChange);
        WaitQuarantined(abandonedBefore + 1);
    }

    [Fact]
    public void NFR01_Live_threads_never_exceed_the_budget_through_repeated_wedges_and_trials()
    {
        _supervisor.Start();
        for (int i = 0; i < 2; i++)
        {
            WedgeOnce();
            _supervisor.Retry().ShouldBe(RetryResult.TrialStarted);

            _guard.LiveThreads(TrackerThreadKind.Worker).ShouldBe(1);
            _guard.LiveThreads(TrackerThreadKind.Watchdog).ShouldBe(1);
            _guard.OwnedThreads.ShouldBeLessThanOrEqualTo(_guard.Budget.MaxOwnedAtOnce);
        }

        _guard.AbandonedThreads.ShouldBe(2);
    }

    [Fact]
    public void NFR01_P6_The_watchdog_measures_every_cancel_and_logs_a_slow_one_once()
    {
        _supervisor.Start();
        _cancelDelayMs = 80;                                   // a cancel far slower than the ~1 ms measured (04 H, P6)

        WedgeOnce();                                           // abandoned even though the cancel itself was slow

        _watchdog.MaxCancelCallMs.ShouldBeGreaterThanOrEqualTo(70);
        _log.Count(m => m.Contains("CoCancelCall took", StringComparison.Ordinal)).ShouldBe(1);
    }

    [Fact]
    public void NFR01_P6_A_fast_cancel_is_not_reported()
    {
        _supervisor.Start();

        WedgeOnce();

        _watchdog.MaxCancelCallMs.ShouldBeLessThan(50);
        _log.ShouldNotContain(m => m.Contains("CoCancelCall took", StringComparison.Ordinal));
    }

    // ---- trials (04 § M.5) ---------------------------------------------------------------------------

    private static readonly QuarantineOptions FastTrials = new()
    {
        CheckInterval = TimeSpan.FromMilliseconds(20),
        HealthHold = TimeSpan.FromMilliseconds(150),
        MinimumQuarantine = TimeSpan.FromMilliseconds(300),
        TrialDeadline = TimeSpan.FromMilliseconds(400),
    };

    [Fact]
    public void NFR01_M5_After_the_hold_one_automatic_trial_runs_and_passes_on_a_full_enumeration_clearing_the_record()
    {
        Reset(FastTrials);
        _supervisor.Start();
        var abandonedAt = System.Diagnostics.Stopwatch.StartNew();
        WedgeOnce();
        _enumerateOnStart = 1;                                 // the trial will connect and enumerate successfully

        WaitFor(() => _machine.Phase == QuarantinePhase.Trial, "automatic trial");
        abandonedAt.Elapsed.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(300));   // not before the minimum quarantine
        _guard.Quarantine.HasUnresolved.ShouldBeTrue();        // starting a trial does not clear quarantine
        _machine.Availability.ShouldBe(TrackerAvailability.RecoveryAttempt);

        WaitFor(() => _machine.State == TrackerState.Running, "trial passed at its deadline");
        _guard.Quarantine.HasUnresolved.ShouldBeFalse();
        _guard.Quarantine.Releases.ByTrial.ShouldBe(1);
        _supervisor.Trials.Started.ShouldBe(1);
        _supervisor.Current.ShouldNotBeNull();                 // the trial worker became the normal worker
        _workersCreated.ShouldBe(2);
    }

    [Fact]
    public void NFR01_M5_A_trial_without_a_full_enumeration_is_inconclusive_and_the_same_server_gets_no_second_automatic_trial()
    {
        Reset(FastTrials);
        _supervisor.Start();
        WedgeOnce();

        WaitFor(() => _supervisor.Trials.Inconclusive == 1, "trial reached its deadline without evidence");
        _machine.State.ShouldBe(TrackerState.Quarantined);
        _machine.Phase.ShouldBe(QuarantinePhase.Waiting);
        _guard.LiveThreads(TrackerThreadKind.Worker).ShouldBe(0);

        Thread.Sleep(1200);                                    // several holds and minimums: the same server is not tried again
        _supervisor.Trials.Started.ShouldBe(1);
        _workersCreated.ShouldBe(2);
        _guard.AbandonedThreads.ShouldBe(1);                   // a cleanly stopped trial is not abandoned
    }

    [Fact]
    public void NFR01_M5_A_trial_that_wedges_returns_to_quarantine_and_repeated_failures_stay_bounded()
    {
        Reset(FastTrials);
        _supervisor.Start();
        WedgeOnce();
        _blockNextWakes = 1;                                   // the trial worker will wedge on its first wake

        WaitFor(() => _machine.Phase == QuarantinePhase.Trial, "automatic trial");
        _router.Raise(TrackerSignal.NameChange);
        WaitQuarantined(2);

        _supervisor.Trials.Wedged.ShouldBe(1);
        Thread.Sleep(1200);                                    // same (known) server: no second automatic trial
        _supervisor.Trials.Started.ShouldBe(1);
        _guard.AbandonedThreads.ShouldBe(2);
        _guard.LiveThreads(TrackerThreadKind.Worker).ShouldBe(0);
    }

    [Fact]
    public void NFR01_M6_Q21_Retry_is_refused_while_the_suspect_shows_hang_evidence_and_allowed_when_it_does_not()
    {
        var server = _probe.Run(4242);
        _supervisor.Start();
        WedgeOnce();
        _probe.Hang(4242);

        _supervisor.Retry().ShouldBe(RetryResult.ServerNotResponding);
        _supervisor.Current.ShouldBeNull();

        _probe.Hang(4242, false);
        _supervisor.Retry().ShouldBe(RetryResult.TrialStarted);
        _supervisor.Current.ShouldNotBeNull();
        _supervisor.Retry().ShouldBe(RetryResult.TrialInProgress);
        server.Pid.ShouldBe(4242u);
    }

    [Fact]
    public void NFR01_M6_Q15_Q17_Stop_and_Start_cannot_bypass_the_quarantine()
    {
        _supervisor.Start();
        WedgeOnce();

        _supervisor.Stop();
        Build(TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(400));
        int created = _workersCreated;

        _supervisor.Start().ShouldBe(StartResult.Quarantined);

        _supervisor.Current.ShouldBeNull();
        _workersCreated.ShouldBe(created);
        _machine.State.ShouldBe(TrackerState.Quarantined);
        _block.Set();                                          // evidence arrives after the restart
        WaitFor(() => _machine.State == TrackerState.Running && _supervisor.Current is not null, "resumed after E1");
    }

    // ---- breaker, Retry, cap -----------------------------------------------------------------------

    [Fact]
    public void NFR01_Three_wedges_trip_the_breaker_leave_no_worker_and_one_Retry_works()
    {
        _supervisor.Start();
        _publisher.TryPublish(_publisher.Generation, _publisher.NextSequence(), new ActiveFolderState(1, 1, @"C:\a", 0));
        WedgeOnce();
        _supervisor.Retry().ShouldBe(RetryResult.TrialStarted);
        WedgeOnce();
        _supervisor.Retry().ShouldBe(RetryResult.TrialStarted);
        WedgeOnce();

        _machine.State.ShouldBe(TrackerState.Disabled);
        _machine.DisabledReason.ShouldBe(DisabledReason.CircuitBreaker);
        _supervisor.Current.ShouldBeNull();
        _router.IsAttached.ShouldBeFalse();                    // nothing is left pointing at an abandoned worker
        _guard.LiveThreads(TrackerThreadKind.Worker).ShouldBe(0);
        _machine.CanRetry.ShouldBeTrue();
        _delivered[^1].IsInactive.ShouldBeTrue();

        _supervisor.Retry().ShouldBe(RetryResult.TrialStarted);   // the servers are unresolved: the retry is a trial (Q22)
        _supervisor.Current.ShouldNotBeNull();
        _router.Raise(TrackerSignal.Foreground);
        WaitFor(() => _wakes.Any(w => w.Generation == _publisher.Generation), "worker after Retry");
        _guard.AbandonedThreads.ShouldBe(3);                   // Retry did not reset the lifetime count
    }

    [Fact]
    public void NFR01_Q1_Six_abandoned_workers_end_in_permanent_exhaustion_and_Retry_is_refused()
    {
        _supervisor.Start();
        for (int i = 0; i < 6; i++)
        {
            if (i > 0)
            {
                _supervisor.Retry().ShouldBe(RetryResult.TrialStarted);
            }

            WedgeOnce();
        }

        _machine.IsPermanentlyDisabled.ShouldBeTrue();
        _machine.DisabledReason.ShouldBe(DisabledReason.ThreadCap);
        _machine.CanRetry.ShouldBeFalse();
        _supervisor.Retry().ShouldBe(RetryResult.ThreadCapReached);
        _supervisor.Current.ShouldBeNull();
        _router.IsAttached.ShouldBeFalse();
        _guard.AbandonedThreads.ShouldBe(6);
        _guard.LiveThreads(TrackerThreadKind.Worker).ShouldBe(0);
        _guard.OwnedThreads.ShouldBeLessThanOrEqualTo(_guard.Budget.MaxOwnedAtOnce);
        _supervisor.Start().ShouldBe(StartResult.Disabled);    // and stays so
        _supervisor.Replacements.ShouldBe(5);                  // five trials, never more than the cap allows
    }

    // ---- start and stop bounds -------------------------------------------------------------------

    [Fact]
    public void NFR01_A_second_tracker_in_the_process_is_refused_and_creates_no_thread()
    {
        _supervisor.Start().ShouldBe(StartResult.Started);
        var other = new TrackerStateMachine(TimeProvider.System, new StatePublisher(_ => { }), _guard);
        int created = _workersCreated;

        other.Start().ShouldBe(StartResult.AnotherTrackerActive);

        _workersCreated.ShouldBe(created);
        _guard.LiveThreads(TrackerThreadKind.Worker).ShouldBe(1);
    }

    [Fact]
    public void NFR01_M_A_worker_that_never_finishes_starting_counts_as_a_wedge_and_is_not_replaced()
    {
        _failNextStarts = 1;

        _supervisor.Start().ShouldBe(StartResult.Started);

        _supervisor.Current.ShouldBeNull();
        _supervisor.Replacements.ShouldBe(0);
        _guard.AbandonedThreads.ShouldBe(1);
        _workersCreated.ShouldBe(1);                           // no start-retry loop
        _machine.State.ShouldBe(TrackerState.Quarantined);
    }

    [Fact]
    public void NFR01_Stop_returns_within_its_bounds_even_while_the_worker_is_wedged_and_counts_the_zombie()
    {
        _supervisor.Start();
        _blockNextWakes = 1;
        _router.Raise(TrackerSignal.NameChange);
        WaitFor(() => _wakes.Count == 1, "worker is inside the stuck evaluation");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        bool clean = _supervisor.Stop();                       // races the watchdog on purpose

        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(3));  // ≤ stop timeout (0.4 s here) + watchdog stop, never unbounded
        clean.ShouldBeFalse();
        _machine.State.ShouldBeOneOf(TrackerState.Stopped, TrackerState.Disabled);
        _guard.AbandonedThreads.ShouldBe(1);                   // counted exactly once, whichever path found it
        _guard.LiveThreads(TrackerThreadKind.Worker).ShouldBe(0);
        _guard.LiveThreads(TrackerThreadKind.Watchdog).ShouldBe(0);
    }

    [Fact]
    public void NFR01_Stop_then_Start_gives_a_fresh_worker_and_frees_every_live_slot_in_between()
    {
        _supervisor.Start();
        _supervisor.Stop().ShouldBeTrue();

        _guard.LiveThreads(TrackerThreadKind.Worker).ShouldBe(0);
        _guard.LiveThreads(TrackerThreadKind.Watchdog).ShouldBe(0);
        _router.IsAttached.ShouldBeFalse();
        _machine.State.ShouldBe(TrackerState.Stopped);

        Build(TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(400));   // the supervisor/watchdog pair is reusable per Start cycle
        _supervisor.Start().ShouldBe(StartResult.Started);
        _router.Raise(TrackerSignal.Foreground);
        WaitFor(() => WakesBy(0) >= 1, "worker after restart");
        _guard.AbandonedThreads.ShouldBe(0);
    }
}
