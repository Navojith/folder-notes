using System.Collections.Concurrent;
using FolderNotes.App.Explorer;
using FolderNotes.App.Explorer.Native;
using FolderNotes.App.Services;
using Microsoft.Extensions.DependencyInjection;

namespace FolderNotes.App.Tests.Explorer;

internal sealed class FakeUi : IUiDispatcher
{
    private readonly ConcurrentQueue<Action> _queue = new();

    public int Posts;

    public bool Accepting = true;

    public bool Post(Action action)
    {
        if (!Accepting)
        {
            return false;
        }

        Interlocked.Increment(ref Posts);
        _queue.Enqueue(action);
        return true;
    }

    /// <summary>Runs the queued UI work on the calling thread (the test thread plays the UI thread).</summary>
    public int Pump()
    {
        int n = 0;
        while (_queue.TryDequeue(out var a))
        {
            a();
            n++;
        }

        return n;
    }
}

internal sealed class FakeWinEvents : IWinEventApi
{
    private int _next = 1000;

    public ConcurrentDictionary<nint, (uint Min, uint Max, uint Pid, WinEventCallback Proc)> Hooks { get; } = new();

    public int Installs;

    public int Unhooks;

    public nint SetHook(uint eventMin, uint eventMax, uint processId, WinEventCallback callback)
    {
        Interlocked.Increment(ref Installs);
        nint h = Interlocked.Increment(ref _next);
        Hooks[h] = (eventMin, eventMax, processId, callback);
        return h;
    }

    public bool Unhook(nint hook)
    {
        Interlocked.Increment(ref Unhooks);
        return Hooks.TryRemove(hook, out _);
    }

    public void Fire(uint ev)
    {
        foreach (var h in Hooks.Values.Where(h => ev >= h.Min && ev <= h.Max).ToArray())
        {
            h.Proc(0, ev, 1, 0, 0, 0, 0);
        }
    }
}

/// <summary>
/// The whole composition with real threads and a fake desktop: events through a real hook thread, a real STA worker, the
/// watchdog, the supervisor and the UI hand-off. Limits are scaled down (60/120 ms cancel/abandon).
/// </summary>
public sealed class ExplorerTrackerTests : IDisposable
{
    private readonly FakeExplorerDesktop _desk = new();
    private readonly FakeWinEvents _api = new();
    private readonly FakeUi _ui = new();
    private readonly TrackerProcessGuard _guard = new();
    private readonly List<ActiveFolderState> _events = [];
    private readonly List<string> _log = [];
    private readonly ExplorerTracker _tracker;
    private readonly int _uiThread = Environment.CurrentManagedThreadId;
    private int _eventThread;

    public ExplorerTrackerTests() => _tracker = Make(_guard);

    private ExplorerTracker Make(TrackerProcessGuard guard)
    {
        var t = new ExplorerTracker(
            _ui, _desk, _desk, _api, guard, null,
            new ExplorerTrackerOptions
            {
                Watchdog = new WatchdogOptions { CancelAfter = TimeSpan.FromMilliseconds(60), AbandonAfterCancel = TimeSpan.FromMilliseconds(120) },
                Hook = new HookThreadOptions { StartTimeout = TimeSpan.FromSeconds(2), StopTimeout = TimeSpan.FromMilliseconds(500) },
                WorkerStartTimeout = TimeSpan.FromSeconds(2),
                WorkerStopTimeout = TimeSpan.FromMilliseconds(500),
            },
            _log.Add);
        t.ActiveFolderChanged += (_, e) =>
        {
            _eventThread = Environment.CurrentManagedThreadId;
            _events.Add(e.State);
        };
        return t;
    }

    public void Dispose()
    {
        _desk.BlockReads?.Set();                               // free zombies
        _tracker.Dispose();
    }

    private static void WaitFor(Func<bool> condition, string what, int ms = 8000) =>
        SpinWait.SpinUntil(condition, TimeSpan.FromMilliseconds(ms)).ShouldBeTrue($"timed out waiting for: {what}");

    private void WaitUi(Func<bool> condition, string what)
    {
        SpinWait.SpinUntil(() => { _ui.Pump(); return condition(); }, TimeSpan.FromSeconds(8))
            .ShouldBeTrue($"timed out waiting for: {what}. events: [{string.Join(", ", _events.Select(e => e.IsInactive ? "Inactive" : e.Path))}] log: [{string.Join(" | ", _log)}] stats: {_tracker.GetStats()}");
    }

    private void OneFrame(string path = @"C:\a")
    {
        _desk.AddTab(1, 11, path, true, 500);
        _desk.Foreground = 1;
    }

    // ---- construction, start, stop ----------------------------------------------------------------------

    [Fact]
    public void NFR06_Constructing_the_tracker_creates_no_thread_no_hook_and_no_COM_object()
    {
        _guard.OwnedThreads.ShouldBe(0);
        _api.Installs.ShouldBe(0);
        _desk.GatewaysCreated.ShouldBe(0);
        _tracker.GetStats().State.ShouldBe(TrackerState.Stopped);
        _tracker.Current.IsInactive.ShouldBeTrue();
    }

    [Fact]
    public void REQ031_Start_publishes_the_foreground_folder_to_the_UI_thread_and_COM_stays_on_the_worker()
    {
        OneFrame(@"C:\a");

        _tracker.Start().ShouldBe(StartResult.Started);
        WaitUi(() => _events.Count == 1, "first event");

        _events[0].Path.ShouldBe(@"C:\a");
        _eventThread.ShouldBe(_uiThread);                      // raised where the dispatcher runs, never on the worker
        _tracker.Current.Path.ShouldBe(@"C:\a");
        _guard.LiveThreads(TrackerThreadKind.Worker).ShouldBe(1);
        _guard.LiveThreads(TrackerThreadKind.Hook).ShouldBe(1);
        _guard.LiveThreads(TrackerThreadKind.Watchdog).ShouldBe(1);
        _guard.OwnedThreads.ShouldBe(3);                       // the whole running budget: 1 worker + 1 hook + 1 watchdog
        _tracker.GetStats().HooksActive.ShouldBeTrue();
    }

    [Fact]
    public void REQ031_A_WinEvent_through_the_real_hook_thread_reaches_the_worker_and_publishes_the_new_folder()
    {
        OneFrame(@"C:\a");
        _tracker.Start();
        WaitUi(() => _events.Count == 1, "first event");
        WaitFor(() => _api.Hooks.Count == 5, "scoped hooks installed on the hook thread");   // foreground + 4 scoped for pid 500
        _api.Hooks.Values.Count(h => h.Pid == 500).ShouldBe(4);

        _desk.Navigate(1, @"C:\b");
        _api.Fire(WinEventMap.ObjectNameChange);
        WaitUi(() => _events.Count == 2, "second event");

        _events[1].Path.ShouldBe(@"C:\b");
        _events[1].Sequence.ShouldBeGreaterThan(_events[0].Sequence);
    }

    [Fact]
    public void REQ031_Delivery_is_latest_wins_a_slow_UI_sees_one_event_with_the_newest_state()
    {
        OneFrame(@"C:\a");
        _tracker.Start();
        WaitUi(() => _events.Count == 1, "first event");
        WaitFor(() => _api.Hooks.Count == 5, "scoped hooks");
        int posts = _ui.Posts;
        long published = _tracker.GetStats().PublishedStates;

        for (int i = 0; i < 5; i++)                            // the UI thread is busy: it does not pump during this
        {
            _desk.Navigate(1, $@"C:\n{i}");
            _api.Fire(WinEventMap.ObjectNameChange);
            long target = published + i + 1;
            WaitFor(() => _tracker.GetStats().PublishedStates >= target, $"state {i} published");
        }

        (_ui.Posts - posts).ShouldBe(1);                       // one queued task however many states arrived
        _ui.Pump();
        _events.Count.ShouldBe(2);
        _events[^1].Path.ShouldBe(@"C:\n4");
    }

    [Fact]
    public void NFR01_Stop_is_bounded_publishes_Inactive_removes_hooks_and_frees_every_thread_and_handle()
    {
        OneFrame();
        _tracker.Start();
        WaitUi(() => _events.Count == 1, "first event");
        WaitFor(() => _api.Hooks.Count == 5, "scoped hooks");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        _tracker.Stop();

        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(3));
        WaitUi(() => _events.Count == 2, "Inactive event");
        _events[^1].IsInactive.ShouldBeTrue();
        _api.Hooks.ShouldBeEmpty();
        _guard.OwnedThreads.ShouldBe(0);
        _desk.LiveHandles.ShouldBe(0);
        _desk.LiveGateways.ShouldBe(0);
        _tracker.GetStats().HooksActive.ShouldBeFalse();
        _tracker.GetStats().State.ShouldBe(TrackerState.Stopped);
        _tracker.Stop();                                       // idempotent

        _tracker.Start().ShouldBe(StartResult.Started);        // a restart begins with an empty cache and works
        WaitUi(() => _events.Count == 3, "event after restart");
        _events[^1].Path.ShouldBe(@"C:\a");
    }

    [Fact]
    public void NFR01_A_second_tracker_in_the_process_is_refused_and_starts_no_hook_thread()
    {
        OneFrame();
        _tracker.Start().ShouldBe(StartResult.Started);
        WaitFor(() => _api.Hooks.Count == 5, "scoped hooks installed (asynchronously, on the hook thread)");
        using var other = Make(_guard);
        int installs = _api.Installs;

        other.Start().ShouldBe(StartResult.AnotherTrackerActive);

        _api.Installs.ShouldBe(installs);
        _guard.LiveThreads(TrackerThreadKind.Hook).ShouldBe(1);
        _guard.LiveThreads(TrackerThreadKind.Worker).ShouldBe(1);
    }

    [Fact]
    public void NFR01_Dispose_is_idempotent_and_a_disposed_tracker_cannot_start()
    {
        OneFrame();
        _tracker.Start();
        _tracker.Dispose();
        _tracker.Dispose();

        _tracker.Start().ShouldBe(StartResult.Disabled);
        _guard.OwnedThreads.ShouldBe(0);
    }

    [Fact]
    public void NFR01_A_UI_queue_that_is_shutting_down_drops_events_silently()
    {
        OneFrame();
        _ui.Accepting = false;

        Should.NotThrow(() => _tracker.Start());
        WaitFor(() => _tracker.GetStats().PublishedStates == 1, "state published to the gate");

        _events.ShouldBeEmpty();
        _tracker.Current.Path.ShouldBe(@"C:\a");              // Current is still right
    }

    // ---- recovery through the whole stack -----------------------------------------------------------------

    [Fact]
    public void NFR01_F_NotifyShellRestarted_goes_Inactive_and_comes_back_without_user_action()
    {
        OneFrame(@"C:\a");
        _tracker.Start();
        WaitUi(() => _events.Count == 1, "first event");
        long recoveries = _tracker.GetStats().Recoveries;

        _tracker.NotifyShellRestarted();
        WaitUi(() => _events.Count >= 3, "Inactive then the folder again");

        _events[1].IsInactive.ShouldBeTrue();
        _events[2].Path.ShouldBe(@"C:\a");
        _tracker.GetStats().Recoveries.ShouldBe(recoveries + 1);
        _tracker.GetStats().State.ShouldBe(TrackerState.Running);
        _desk.GatewaysCreated.ShouldBe(2);
    }

    // ---- wedge, replacement, breaker, cap ------------------------------------------------------------------

    private void Wedge()
    {
        int before = _tracker.GetStats().AbandonedWorkers;
        _desk.BlockReads ??= new ManualResetEventSlim(false);
        Interlocked.Exchange(ref _desk.BlockNextReads, 1);
        _api.Fire(WinEventMap.ObjectNameChange);               // the worker will block inside the next path read
        WaitFor(() => _tracker.GetStats().AbandonedWorkers == before + 1, "worker abandoned");
        WaitFor(() => _tracker.GetStats().State is TrackerState.Disabled || _tracker.GetStats().Replacements > 0, "decision applied");
    }

    [Fact]
    public void NFR01_B6_A_wedged_worker_is_replaced_without_touching_the_hooks_and_its_late_result_is_dropped()
    {
        OneFrame(@"C:\a");
        _tracker.Start();
        WaitUi(() => _events.Count == 1, "first event");
        WaitFor(() => _api.Hooks.Count == 5, "scoped hooks");
        int installs = _api.Installs;
        int unhooks = _api.Unhooks;
        var firstWorker = _desk.GatewaysCreated;

        _desk.BlockReads = new ManualResetEventSlim(false);
        Interlocked.Exchange(ref _desk.BlockNextReads, 1);
        _api.Fire(WinEventMap.ObjectNameChange);
        WaitFor(() => _tracker.GetStats().Replacements == 1, "replacement");

        var stats = _tracker.GetStats();
        stats.CancelCalls.ShouldBe(1);
        stats.AbandonedWorkers.ShouldBe(1);
        stats.State.ShouldBe(TrackerState.Running);
        _api.Installs.ShouldBe(installs);                      // the hooks were never reinstalled or removed
        _api.Unhooks.ShouldBe(unhooks);
        _guard.LiveThreads(TrackerThreadKind.Worker).ShouldBe(1);
        WaitUi(() => _events.Count >= 2 && _events[^1].Path == @"C:\a", "the replacement's state");
        // The Inactive published at the abandon was superseded before the UI pumped (latest-wins); the supervisor tests assert it at the gate.
        _events[^1].Sequence.ShouldBeGreaterThan(_events[0].Sequence);
        _desk.GatewaysCreated.ShouldBe(firstWorker + 1);       // a fresh root and an empty cache for the replacement

        _desk.Navigate(1, @"C:\after");
        _api.Fire(WinEventMap.ObjectNameChange);
        WaitUi(() => _events[^1].Path == @"C:\after", "replacement publishes the next change");

        int eventCount = _events.Count;
        _desk.BlockedReadResultPath = @"C:\zombie";                // what only the stuck worker's read can return
        _desk.BlockReads.Set();                                // the zombie returns and tries to publish with the old generation
        WaitFor(() => _desk.BlockedReadsCompleted == 1, "the zombie's read finished");
        Thread.Sleep(400);                                     // it publishes (and is refused) well within this
        _ui.Pump();
        _events.Count.ShouldBe(eventCount);
        _events.ShouldNotContain(e => e.Path == @"C:\zombie");
        _tracker.Current.Path.ShouldBe(@"C:\after");              // the truth was never overwritten
    }

    [Fact]
    public void NFR01_Q1_Breaker_then_one_Retry_then_permanent_exhaustion_through_the_whole_stack()
    {
        OneFrame(@"C:\a");
        _tracker.Start();
        WaitUi(() => _events.Count == 1, "first event");
        WaitFor(() => _api.Hooks.Count == 5, "scoped hooks");

        Wedge(); Wedge(); Wedge();

        var s = _tracker.GetStats();
        s.State.ShouldBe(TrackerState.Disabled);
        s.DisabledReason.ShouldBe(DisabledReason.CircuitBreaker);
        s.CanRetry.ShouldBeTrue();
        s.PermanentlyDisabled.ShouldBeFalse();
        _guard.LiveThreads(TrackerThreadKind.Worker).ShouldBe(0);
        _tracker.Current.IsInactive.ShouldBeTrue();
        _tracker.Start().ShouldBe(StartResult.Disabled);       // switching it on again is not a Retry

        _tracker.Retry().ShouldBe(RetryResult.Started);
        WaitFor(() => _tracker.GetStats().State == TrackerState.Running && _guard.LiveThreads(TrackerThreadKind.Worker) == 1, "running again");
        WaitUi(() => _tracker.Current.Path == @"C:\a", "state after Retry");

        Wedge(); Wedge(); Wedge();

        s = _tracker.GetStats();
        s.PermanentlyDisabled.ShouldBeTrue();
        s.DisabledReason.ShouldBe(DisabledReason.ThreadCap);
        s.CanRetry.ShouldBeFalse();
        s.AbandonedWorkers.ShouldBe(6);
        _tracker.Retry().ShouldBe(RetryResult.ThreadCapReached);
        _tracker.Start().ShouldBe(StartResult.Disabled);
        _guard.OwnedThreads.ShouldBeLessThanOrEqualTo(_guard.Budget.MaxOwnedAtOnce);
        _tracker.GetStats().AbandonedWorkers.ShouldBe(6);      // nothing buys back a thread
    }

    [Fact]
    public void NFR01_Stop_stays_bounded_while_the_worker_is_wedged_and_the_zombie_is_counted_once()
    {
        OneFrame();
        _tracker.Start();
        WaitUi(() => _events.Count == 1, "first event");
        WaitFor(() => _api.Hooks.Count == 5, "scoped hooks");
        _desk.BlockReads = new ManualResetEventSlim(false);
        Interlocked.Exchange(ref _desk.BlockNextReads, 1);
        _api.Fire(WinEventMap.ObjectNameChange);
        WaitFor(() => _desk.Reads >= 2, "worker is inside the stuck read");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        _tracker.Stop();

        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(4));
        _tracker.GetStats().AbandonedWorkers.ShouldBe(1);
        _guard.LiveThreads(TrackerThreadKind.Worker).ShouldBe(0);
        _guard.LiveThreads(TrackerThreadKind.Hook).ShouldBe(0);
        _guard.LiveThreads(TrackerThreadKind.Watchdog).ShouldBe(0);
        _api.Hooks.ShouldBeEmpty();
    }

    [Fact]
    public void NFR01_An_exception_in_the_gateway_is_survived_by_the_real_worker()
    {
        OneFrame(@"C:\a");
        _tracker.Start();
        WaitUi(() => _events.Count == 1, "first event");
        WaitFor(() => _api.Hooks.Count == 5, "scoped hooks");

        _desk.Navigate(1, @"C:\b");
        _desk.ThrowOnNextRead = true;
        _api.Fire(WinEventMap.ObjectNameChange);

        WaitUi(() => _events.Count == 2 && _events[^1].Path == @"C:\b", "state after the failed read was retried");
        _tracker.GetStats().AbandonedWorkers.ShouldBe(0);
    }

    // ---- stats, DI ---------------------------------------------------------------------------------------

    [Fact]
    public void NFR01_Stats_report_evaluations_percentiles_and_the_thread_and_hook_state()
    {
        OneFrame();
        _tracker.Start();
        WaitUi(() => _events.Count == 1, "first event");

        var s = _tracker.GetStats();

        s.Evaluations.ShouldBeGreaterThan(0);
        s.PublishedStates.ShouldBe(1);
        s.EvaluationP50Ms.ShouldBeGreaterThanOrEqualTo(0);
        s.EvaluationMaxMs.ShouldBeGreaterThanOrEqualTo(s.EvaluationP95Ms);
        s.EvaluationP95Ms.ShouldBeGreaterThanOrEqualTo(s.EvaluationP50Ms);
        s.CrossThreadReleases.ShouldBe(0);
        s.HooksActive.ShouldBeTrue();
        s.State.ShouldBe(TrackerState.Running);
    }

    [Fact]
    public void NFR01_The_DI_registration_resolves_one_singleton_that_is_not_started()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IUiDispatcher>(_ui);
        services.AddExplorerTracker();
        using var provider = services.BuildServiceProvider();

        var a = provider.GetRequiredService<IExplorerTracker>();
        var b = provider.GetRequiredService<IExplorerTracker>();

        a.ShouldBeSameAs(b);
        a.GetStats().State.ShouldBe(TrackerState.Stopped);
        a.Current.IsInactive.ShouldBeTrue();
        TrackerProcessGuard.Shared.OwnedThreads.ShouldBe(0);   // resolving started nothing
    }
}
