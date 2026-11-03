using FolderNotes.App.Explorer;
using FolderNotes.App.Explorer.Native;

namespace FolderNotes.App.Tests.Explorer;

/// <summary>
/// 04 B.2, B.6, B.7 with real threads: the STA worker host, the watchdog thread and the supervisor (cancel → abandon →
/// replace → breaker → cap). Time limits are scaled down (60 ms cancel, 120 ms abandon) so a wedge takes ~200 ms.
/// </summary>
public sealed class WorkerSupervisorTests : IDisposable
{
    private static readonly WatchdogOptions Quick = new() { CancelAfter = TimeSpan.FromMilliseconds(60), AbandonAfterCancel = TimeSpan.FromMilliseconds(120) };

    private readonly TrackerProcessGuard _guard = new();
    private readonly SignalRouter _router = new();
    private readonly List<ActiveFolderState> _delivered = [];
    private readonly StatePublisher _publisher;
    private readonly TrackerStateMachine _machine;
    private readonly ManualResetEventSlim _block = new(false);   // releasing it frees every "stuck" worker at the end
    private readonly List<uint> _cancelled = [];
    private readonly List<(int Generation, int Thread, TrackerSignal Bits)> _wakes = [];
    private readonly List<string> _log = [];
    private readonly Lock _lock = new();
    private WatchdogThread _watchdog = null!;
    private WorkerSupervisor _supervisor = null!;
    private int _workersCreated;
    private int _blockNextWakes;                              // how many wake-ups will hang
    private int _failNextStarts;                              // how many workers hang in onStart
    private int _throwNextWakes;
    private int _cancelDelayMs;                               // makes CoCancelCall "slow" to exercise the instrumentation
    private readonly List<(int Generation, int Thread, ApartmentState Apartment)> _starts = [];
    private readonly List<(int Generation, int Thread)> _stops = [];

    public WorkerSupervisorTests()
    {
        _publisher = new StatePublisher(_delivered.Add);
        _machine = new TrackerStateMachine(TimeProvider.System, _publisher, _guard);
        Build(startTimeout: TimeSpan.FromMilliseconds(300), stopTimeout: TimeSpan.FromMilliseconds(400));
    }

    private void Build(TimeSpan startTimeout, TimeSpan stopTimeout)
    {
        _watchdog = new WatchdogThread(_guard, w => _supervisor.OnWorkerWedged(w), tid => { if (_cancelDelayMs > 0) { Thread.Sleep(_cancelDelayMs); } lock (_lock) { _cancelled.Add(tid); } }, m => _log.Add(m));
        _supervisor = new WorkerSupervisor(_machine, _router, _publisher, _watchdog, CreateWorker, m => _log.Add(m), startTimeout, stopTimeout);
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

    // ---- cancel, abandon, replace -----------------------------------------------------------------

    [Fact]
    public void NFR01_A_stuck_evaluation_is_cancelled_then_abandoned_and_one_replacement_serves_the_next_signal()
    {
        _supervisor.Start();
        var old = _supervisor.Current!;
        _publisher.TryPublish(_publisher.Generation, _publisher.NextSequence(), new ActiveFolderState(1, 1, @"C:\a", 0));
        int genBefore = _publisher.Generation;
        _blockNextWakes = 1;

        _router.Raise(TrackerSignal.NameChange);               // the worker hangs inside this evaluation
        WaitFor(() => _supervisor.Replacements == 1, "replacement");

        _watchdog.CancelCalls.ShouldBe(1);                     // cancel came first, once, aimed at the stuck thread
        _cancelled.ShouldBe([old.ThreadId]);
        _supervisor.Current.ShouldNotBeNull().ShouldNotBeSameAs(old);
        old.IsAbandoned.ShouldBeTrue();
        _publisher.Generation.ShouldBe(genBefore + 1);
        _delivered[^1].IsInactive.ShouldBeTrue();              // the panel was told "nothing" when the worker was abandoned
        _machine.LiveWorkers.ShouldBe(1);
        _guard.LiveThreads(TrackerThreadKind.Worker).ShouldBe(1);
        _guard.AbandonedThreads.ShouldBe(1);

        _router.Raise(TrackerSignal.ShowHide);                 // the next signal goes to the replacement only
        WaitFor(() => WakesBy(genBefore + 1) == 1, "replacement handled the next signal");
        WakesBy(genBefore).ShouldBe(1);                        // the zombie never saw it
    }

    [Fact]
    public void NFR01_An_abandoned_worker_cannot_publish_and_takes_no_signals_even_after_it_returns()
    {
        _supervisor.Start();
        var old = _supervisor.Current!;
        int oldGen = _publisher.Generation;
        _blockNextWakes = 1;
        _router.Raise(TrackerSignal.NameChange);
        WaitFor(() => _supervisor.Replacements == 1, "replacement");

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
        // The supervisor has no reference to the hook thread at all (04 B.7): a replacement only re-points the router.
        typeof(WorkerSupervisor).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Select(f => f.FieldType).ShouldNotContain(typeof(HookThread));
        typeof(StaWorkerHost).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Select(f => f.FieldType).ShouldNotContain(typeof(HookThread));
    }

    [Fact]
    public void NFR01_Live_threads_never_exceed_the_budget_through_repeated_wedges_and_replacements()
    {
        _supervisor.Start();
        for (int i = 0; i < 2; i++)
        {
            _blockNextWakes = 1;
            int before = _supervisor.Replacements;
            _router.Raise(TrackerSignal.NameChange);
            WaitFor(() => _supervisor.Replacements == before + 1, "replacement " + (i + 1));

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

        _blockNextWakes = 1;
        _router.Raise(TrackerSignal.NameChange);
        WaitFor(() => _supervisor.Replacements == 1, "replacement even though the cancel itself was slow");

        _watchdog.MaxCancelCallMs.ShouldBeGreaterThanOrEqualTo(70);
        _log.Count(m => m.Contains("CoCancelCall took", StringComparison.Ordinal)).ShouldBe(1);
    }

    [Fact]
    public void NFR01_P6_A_fast_cancel_is_not_reported()
    {
        _supervisor.Start();

        _blockNextWakes = 1;
        _router.Raise(TrackerSignal.NameChange);
        WaitFor(() => _supervisor.Replacements == 1, "replacement");

        _watchdog.MaxCancelCallMs.ShouldBeLessThan(50);
        _log.ShouldNotContain(m => m.Contains("CoCancelCall took", StringComparison.Ordinal));
    }

    // ---- breaker, Retry, cap -----------------------------------------------------------------------

    private void WedgeOnce()
    {
        int abandonedBefore = _guard.AbandonedThreads;
        _blockNextWakes = 1;
        _router.Raise(TrackerSignal.NameChange);
        WaitFor(() => _guard.AbandonedThreads == abandonedBefore + 1 && (_supervisor.Current is not null || _machine.State == TrackerState.Disabled), "wedge handled");
    }

    [Fact]
    public void NFR01_Three_wedges_trip_the_breaker_leave_no_worker_and_one_Retry_works()
    {
        _supervisor.Start();
        _publisher.TryPublish(_publisher.Generation, _publisher.NextSequence(), new ActiveFolderState(1, 1, @"C:\a", 0));
        WedgeOnce();
        WedgeOnce();
        WedgeOnce();

        _machine.State.ShouldBe(TrackerState.Disabled);
        _machine.DisabledReason.ShouldBe(DisabledReason.CircuitBreaker);
        _supervisor.Current.ShouldBeNull();
        _router.IsAttached.ShouldBeFalse();                    // nothing is left pointing at an abandoned worker
        _guard.LiveThreads(TrackerThreadKind.Worker).ShouldBe(0);
        _machine.CanRetry.ShouldBeTrue();
        _delivered[^1].IsInactive.ShouldBeTrue();

        _supervisor.Retry().ShouldBe(RetryResult.Started);
        _supervisor.Current.ShouldNotBeNull();
        _router.Raise(TrackerSignal.Foreground);
        WaitFor(() => _wakes.Any(w => w.Generation == _publisher.Generation), "worker after Retry");
        _guard.AbandonedThreads.ShouldBe(3);                   // Retry did not reset the lifetime count
    }

    [Fact]
    public void NFR01_Q1_Six_abandoned_workers_end_in_permanent_exhaustion_and_Retry_is_refused()
    {
        _supervisor.Start();
        for (int i = 0; i < 3; i++)
        {
            WedgeOnce();
        }

        _supervisor.Retry().ShouldBe(RetryResult.Started);
        for (int i = 0; i < 3; i++)
        {
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
        _supervisor.Replacements.ShouldBe(4);                  // 2 + 0 (breaker trips) per episode, never more than the cap allows
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
    public void NFR01_A_worker_that_never_finishes_starting_counts_as_a_wedge_and_is_replaced()
    {
        _failNextStarts = 1;

        _supervisor.Start().ShouldBe(StartResult.Started);

        _supervisor.Current.ShouldNotBeNull();
        _supervisor.Replacements.ShouldBe(1);
        _guard.AbandonedThreads.ShouldBe(1);
        _workersCreated.ShouldBe(2);
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
        _machine.State.ShouldBeOneOf(TrackerState.Stopped, TrackerState.Running, TrackerState.Disabled);
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
