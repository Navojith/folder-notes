using FolderNotes.App.Explorer;
using Microsoft.Extensions.Time.Testing;

namespace FolderNotes.App.Tests.Explorer;

/// <summary>A scriptable stand-in for Explorer: frames, tabs, paths, the foreground, injected failures.</summary>
internal sealed class FakeExplorerWorld
{
    public sealed record Tab(nint Frame, nint Key, string? Path, bool Visible);

    public List<Tab> Tabs { get; } = [];

    public nint ForegroundFrame { get; set; }

    /// <summary>HRESULTs the next path reads fail with, in order (0 = succeed).</summary>
    public Queue<int> PathFailures { get; } = [];

    /// <summary>When true the enumeration hides every tab (a private empty ShellWindows server).</summary>
    public bool RootIsEmpty { get; set; }

    public Tab Visible(nint frame) => Tabs.First(t => t.Frame == frame && t.Visible);

    public void Navigate(nint frame, string? path)
    {
        int i = Tabs.FindIndex(t => t.Frame == frame && t.Visible);
        Tabs[i] = Tabs[i] with { Path = path };
    }

    public void SwitchTab(nint frame, nint key)
    {
        for (int i = 0; i < Tabs.Count; i++)
        {
            if (Tabs[i].Frame == frame)
            {
                Tabs[i] = Tabs[i] with { Visible = Tabs[i].Key == key };
            }
        }
    }
}

/// <summary>
/// Wires scheduler + map + publisher + state machine to a <see cref="FakeExplorerWorld"/> the way the T3.2.4 glue
/// will, but with no COM and no threads, so lost events, ordering and recovery can be exercised on a fake clock.
/// </summary>
internal sealed class TrackerRig
{
    public FakeTimeProvider Clock { get; } = new();

    public FakeExplorerWorld World { get; } = new();

    public List<ActiveFolderState> Delivered { get; } = [];

    public StatePublisher Publisher { get; }

    public EvaluationScheduler Scheduler { get; }

    public TabMap<FakeHandle> Map { get; } = new();

    public TrackerStateMachine Machine { get; }

    public LiveCounter Handles { get; } = new();

    public int Generation { get; private set; }

    /// <summary>The replacement worker uses the generation the wedge decision returned.</summary>
    public void AdoptGeneration(int generation) => Generation = generation;

    public int Rebuilds { get; private set; }

    public int Evaluations { get; private set; }

    private readonly DateTimeOffset _start;

    public TrackerRig(TrackerLimits? limits = null)
    {
        Publisher = new StatePublisher(Delivered.Add);
        Scheduler = new EvaluationScheduler(Clock, null, Publisher.NextSequence);
        Machine = new TrackerStateMachine(Clock, Publisher, new TrackerProcessGuard(), limits);
        Machine.Start();
        Generation = Publisher.Generation;
        _start = Clock.GetUtcNow();
    }

    public double NowMs => (Clock.GetUtcNow() - _start).TotalMilliseconds;

    public void Advance(double ms) => Clock.Advance(TimeSpan.FromMilliseconds(ms));

    public void Signal(SignalKinds k = SignalKinds.Navigation) => Scheduler.Signal(k);

    /// <summary>Runs the loop for <paramref name="ms"/> of fake time.</summary>
    public void Run(double ms, double evalCostMs = 0, Action<double>? everyStep = null)
    {
        for (double t = 0; t < ms; t++)
        {
            everyStep?.Invoke(t);
            while (Scheduler.TryTakeDue(out var req))
            {
                Advance(evalCostMs);
                var outcome = Evaluate(req);
                Scheduler.Complete(req.Sequence, outcome);
            }

            Advance(1);
        }
    }

    public EvaluationOutcome Evaluate(EvaluationRequest req)
    {
        Evaluations++;
        nint frame = World.ForegroundFrame;
        if (frame == 0)
        {
            Publisher.TryPublish(Generation, req.Sequence, ActiveFolderState.Inactive());
            return new EvaluationOutcome(false, false, EvaluationResultKind.Resolved);
        }

        var children = World.Tabs.Where(t => t.Frame == frame).Select(t => new ChildTab(t.Key, t.Visible)).ToList();
        var active = ActiveTabResolver.Resolve(frame, children);
        if (active.Kind == ActiveTabKind.Unresolved)
        {
            Publisher.TryPublish(Generation, req.Sequence, null);
            return new EvaluationOutcome(true, false, EvaluationResultKind.Unresolved);
        }

        if (req.Rebuild || !Map.TryGet(active.Key, out _))
        {
            Rebuilds++;
            var found = World.RootIsEmpty
                ? []
                : World.Tabs.Select(t => new DiscoveredTab<FakeHandle>(t.Frame, t.Key, new FakeHandle(Handles))).ToList();
            Map.Reconcile(found, frame);
            if (!Map.TryGet(active.Key, out _))
            {
                Publisher.TryPublish(Generation, req.Sequence, null);
                return new EvaluationOutcome(true, false, EvaluationResultKind.Unresolved);
            }
        }

        if (World.PathFailures.TryDequeue(out int hr) && hr != 0)
        {
            var cls = ComFailureClassifier.Classify(hr, windowAlive: true);
            Map.RecordFailure(active.Key, cls);
            Publisher.TryPublish(Generation, req.Sequence, null);
            return new EvaluationOutcome(true, false, cls == ComFailureClass.Transient ? EvaluationResultKind.Transient : EvaluationResultKind.Unresolved);
        }

        var tab = World.Visible(frame);
        Map.RecordSuccess(active.Key, tab.Path);
        var before = Publisher.Current;
        var result = Publisher.TryPublish(Generation, req.Sequence, new ActiveFolderState(frame, tab.Key, tab.Path, 0));
        return new EvaluationOutcome(true, result == PublishResult.Published && !before.SameFolderAs(Publisher.Current), EvaluationResultKind.Resolved);
    }
}

/// <summary>
/// The pure parts working together (REQ-031 latency and ordering, REQ-034 tabs, D2 lost events, D3 coalescing,
/// D4 abandoned workers, F recovery). Time is fake; "evaluation cost" is simulated.
/// </summary>
public class TrackerLogicSimulationTests
{
    private static TrackerRig OneFrame(string path = @"C:\a")
    {
        var rig = new TrackerRig();
        rig.World.Tabs.Add(new FakeExplorerWorld.Tab(1, 11, path, true));
        rig.World.ForegroundFrame = 1;
        return rig;
    }

    [Fact]
    public void REQ031_Navigation_is_published_at_once_and_within_the_150ms_budget()
    {
        var rig = OneFrame();
        rig.Signal(SignalKinds.Foreground);
        rig.Run(5);
        rig.Delivered.ShouldHaveSingleItem().Path.ShouldBe(@"C:\a");

        rig.Advance(100);
        rig.World.Navigate(1, @"C:\b");
        double at = rig.NowMs;
        rig.Signal();
        rig.Run(1, evalCostMs: 8);                      // an evaluation that costs 8 ms (well over the < 10 ms target's mean)

        rig.Delivered[^1].Path.ShouldBe(@"C:\b");
        (rig.NowMs - at).ShouldBeLessThan(150);
    }

    [Fact]
    public void REQ031_Rapid_navigation_ends_on_the_final_folder_with_bounded_work()
    {
        var rig = OneFrame();
        rig.Signal(SignalKinds.Foreground);
        rig.Run(5);
        int evalsBefore = rig.Evaluations;

        for (int i = 0; i < 200; i++)                   // 200 folder changes, one per millisecond
        {
            rig.World.Navigate(1, $@"C:\f{i}");
            rig.Signal();
            rig.Run(1, evalCostMs: 0);
        }

        rig.Run(200);
        rig.Delivered[^1].Path.ShouldBe(@"C:\f199");   // the final state is always processed
        (rig.Evaluations - evalsBefore).ShouldBeLessThan(200 / 30 + 12);   // ~one per 30 ms (+ confirm / poll), not one per event
        rig.Delivered.Select(d => d.Sequence).ShouldBe(rig.Delivered.Select(d => d.Sequence).OrderBy(x => x));
    }

    [Fact]
    public void REQ031_Unchanged_state_is_never_redelivered_however_many_signals_and_polls()
    {
        var rig = OneFrame();
        rig.Signal(SignalKinds.Foreground);
        for (int i = 0; i < 300; i++)
        {
            rig.Signal();
            rig.Run(7);
        }

        rig.Run(15_000);                                 // plus polls and reconciles
        rig.Delivered.Count.ShouldBe(1);
    }

    [Fact]
    public void REQ034_Switching_between_two_tabs_on_the_same_folder_publishes_nothing_but_different_folders_do()
    {
        var rig = OneFrame(@"C:\same");
        rig.World.Tabs.Add(new FakeExplorerWorld.Tab(1, 12, @"C:\same", false));
        rig.World.Tabs.Add(new FakeExplorerWorld.Tab(1, 13, @"C:\other", false));
        rig.Signal(SignalKinds.Foreground);
        rig.Run(10);

        rig.World.SwitchTab(1, 12); rig.Signal(); rig.Run(50);
        rig.Delivered.Count.ShouldBe(1);

        rig.World.SwitchTab(1, 13); rig.Signal(); rig.Run(50);
        rig.Delivered.Count.ShouldBe(2);
        rig.Delivered[^1].Path.ShouldBe(@"C:\other");
        rig.Delivered[^1].TabHwnd.ShouldBe((nint)13);
    }

    [Fact]
    public void REQ034_A_new_tab_is_a_cache_miss_that_triggers_a_rebuild_and_resolves()
    {
        var rig = OneFrame();
        rig.Signal(SignalKinds.Foreground);
        rig.Run(10);
        rebuildsAre(rig, 1);

        rig.World.Tabs.Add(new FakeExplorerWorld.Tab(1, 12, @"C:\new", false));
        rig.World.SwitchTab(1, 12);
        rig.Signal(SignalKinds.Structure);
        rig.Run(50);

        rebuildsAre(rig, 2);
        rig.Delivered[^1].Path.ShouldBe(@"C:\new");
        static void rebuildsAre(TrackerRig r, int n) => r.Rebuilds.ShouldBe(n);
    }

    [Fact]
    public void REQ034_A_window_registered_late_is_found_by_the_retry_ladder()
    {
        var rig = OneFrame();
        rig.Signal(SignalKinds.Foreground);
        rig.Run(10);
        // a second window becomes foreground before IShellWindows lists it
        rig.World.ForegroundFrame = 2;
        rig.World.Tabs.Add(new FakeExplorerWorld.Tab(2, 21, @"C:\second", true));
        bool registered = false;
        var hidden = rig.World.Tabs[^1];
        rig.World.Tabs.RemoveAt(rig.World.Tabs.Count - 1);
        rig.Signal(SignalKinds.Foreground);

        rig.Run(300, everyStep: t => { });
        // still not listed: Unresolved, ladder running, nothing wrong published
        rig.Delivered[^1].Path.ShouldBe(@"C:\a");
        registered = true;
        rig.World.Tabs.Add(hidden);
        rig.Run(1200);

        registered.ShouldBeTrue();
        rig.Delivered[^1].Path.ShouldBe(@"C:\second");
    }

    [Fact]
    public void REQ031_The_displayed_folder_is_never_older_than_2s_even_if_every_event_is_lost()
    {
        var rig = OneFrame();
        rig.Signal(SignalKinds.Foreground);
        rig.Run(5);

        rig.Run(11_000);                                 // 11 s of silence: the poll is now in its slow mode
        rig.World.Navigate(1, @"C:\silent");            // Explorer navigates and we hear nothing at all
        double changedAt = rig.NowMs;

        rig.Run(2_100);

        rig.Delivered[^1].Path.ShouldBe(@"C:\silent");
        var publishedAt = rig.NowMs; // upper bound: it must have happened inside the 2.1 s above
        (publishedAt - changedAt).ShouldBeLessThanOrEqualTo(2_100);
    }

    [Fact]
    public void REQ031_Staleness_is_at_most_500ms_in_the_first_10s_after_activity_with_no_events()
    {
        var rig = OneFrame();
        rig.Signal(SignalKinds.Foreground);
        rig.Run(5);
        rig.Run(1000);
        rig.World.Navigate(1, @"C:\quiet");
        double changedAt = rig.NowMs;
        double? seenAt = null;

        rig.Run(700, everyStep: _ => { if (seenAt is null && rig.Delivered[^1].Path == @"C:\quiet") { seenAt = rig.NowMs; } });

        seenAt.ShouldNotBeNull();
        (seenAt.Value - changedAt).ShouldBeLessThanOrEqualTo(501);
    }

    [Fact]
    public void REQ031_A_transient_failure_publishes_nothing_wrong_and_the_ladder_recovers_the_state()
    {
        var rig = OneFrame();
        rig.Signal(SignalKinds.Foreground);
        rig.Run(10);

        rig.World.Navigate(1, @"C:\b");
        rig.World.PathFailures.Enqueue(unchecked((int)0x8001010A));   // RETRYLATER
        rig.World.PathFailures.Enqueue(unchecked((int)0x80010001));   // REJECTED
        rig.Signal();
        rig.Run(40);
        rig.Delivered[^1].Path.ShouldBe(@"C:\a");                       // never blank, never wrong, while Explorer is busy

        rig.Run(400);
        rig.Delivered[^1].Path.ShouldBe(@"C:\b");
        rig.Map.Contains(11).ShouldBeTrue();                            // transient never dropped the tab
    }

    [Fact]
    public void REQ031_A_gone_tab_is_dropped_and_rebuilt_without_losing_the_others()
    {
        var rig = OneFrame();
        rig.World.Tabs.Add(new FakeExplorerWorld.Tab(1, 12, @"C:\x", false));
        rig.Signal(SignalKinds.Foreground);
        rig.Run(10);

        rig.World.PathFailures.Enqueue(unchecked((int)0x80010108));   // RPC_E_DISCONNECTED on the active tab
        rig.World.Navigate(1, @"C:\b");
        rig.Signal();
        rig.Run(300);

        rig.Delivered[^1].Path.ShouldBe(@"C:\b");
        rig.Map.Contains(12).ShouldBeTrue();
    }

    [Fact]
    public void REQ033_When_Explorer_loses_the_foreground_the_state_goes_inactive_and_timers_stop()
    {
        var rig = OneFrame();
        rig.Signal(SignalKinds.Foreground);
        rig.Run(10);

        rig.World.ForegroundFrame = 0;
        rig.Signal(SignalKinds.Foreground);
        rig.Run(60);
        rig.Delivered[^1].IsInactive.ShouldBeTrue();

        int evals = rig.Evaluations;
        rig.Run(120_000);
        rig.Evaluations.ShouldBe(evals);                 // not one evaluation in two fake minutes of background
    }

    // ---- Explorer restart (fault injection) -----------------------------------------------------------

    [Fact]
    public void REQ031_Explorer_restart_goes_inactive_then_recovers_without_restarting_the_tracker()
    {
        var rig = OneFrame();
        rig.Signal(SignalKinds.Foreground);
        rig.Run(10);
        rig.Delivered[^1].Path.ShouldBe(@"C:\a");

        // the shell dies: every window is gone, the root reports Gone
        rig.Machine.ShellGone();
        rig.Map.Clear();
        rig.World.Tabs.Clear();
        rig.World.ForegroundFrame = 0;
        rig.Delivered[^1].IsInactive.ShouldBeTrue();
        rig.Machine.State.ShouldBe(TrackerState.Recovering);
        rig.Handles.Live.ShouldBe(0);                    // every reference was released

        // a new shell, a new window
        rig.Machine.NextRecoveryDelay().ShouldBe(TimeSpan.FromMilliseconds(250));
        rig.Machine.RecoverySucceeded();
        rig.World.Tabs.Add(new FakeExplorerWorld.Tab(7, 70, @"C:\after", true));
        rig.World.ForegroundFrame = 7;
        rig.Signal(SignalKinds.Foreground);
        rig.Run(60);

        rig.Machine.State.ShouldBe(TrackerState.Running);
        rig.Delivered[^1].Path.ShouldBe(@"C:\after");
        rig.Delivered[^1].FrameHwnd.ShouldBe((nint)7);
    }

    [Fact]
    public void REQ031_A_private_empty_root_is_detected_by_two_empty_rebuilds_while_frames_exist()
    {
        var rig = OneFrame();
        rig.World.RootIsEmpty = true;
        var validator = new RootValidator();
        rig.Signal(SignalKinds.Foreground);
        rig.Run(1200);                                    // ladder: the active tab can never be found

        rig.Delivered.ShouldBeEmpty();                    // nothing wrong was published
        rig.Rebuilds.ShouldBeGreaterThanOrEqualTo(2);
        validator.Observe(0, true).ShouldBe(RootVerdict.Suspect);
        validator.Observe(0, true).ShouldBe(RootVerdict.ReplaceRoot);
    }

    // ---- abandoned worker -----------------------------------------------------------------------------

    [Fact]
    public void NFR01_A_wedged_worker_is_replaced_and_its_late_result_never_reaches_the_UI()
    {
        var rig = OneFrame();
        rig.Signal(SignalKinds.Foreground);
        rig.Run(10);
        int zombieGeneration = rig.Generation;
        long zombieSeq = rig.Publisher.NextSequence();    // the doomed worker started an evaluation...

        var wedge = rig.Machine.OnWedge();                // ...and the watchdog gave up on it
        rig.Delivered[^1].IsInactive.ShouldBeTrue();
        rig.Machine.ReplacementStarted();
        rig.World.Navigate(1, @"C:\fresh");

        // the zombie finally returns with what it read long ago
        rig.Publisher.TryPublish(zombieGeneration, zombieSeq, new ActiveFolderState(1, 11, @"C:\stale", 0))
            .ShouldBe(PublishResult.StaleGeneration);

        // the replacement serves the same signal from an empty cache
        rig.Map.Clear();
        rig.AdoptGeneration(wedge.Generation);
        rig.Signal(SignalKinds.Foreground);
        rig.Run(60);

        rig.Delivered.Select(d => d.Path).ShouldNotContain(@"C:\stale");
        rig.Delivered[^1].Path.ShouldBe(@"C:\fresh");
    }

    [Fact]
    public void NFR01_No_handle_survives_Stop_after_a_busy_session()
    {
        var rig = OneFrame();
        for (int f = 2; f < 12; f++)
        {
            rig.World.Tabs.Add(new FakeExplorerWorld.Tab(f, f * 10, $@"C:\w{f}", true));
        }

        rig.Signal(SignalKinds.Foreground);
        for (int f = 1; f < 12; f++)
        {
            rig.World.ForegroundFrame = f;
            rig.Signal(SignalKinds.Foreground);
            rig.Run(40);
        }

        rig.Map.Count.ShouldBeGreaterThan(0);
        rig.Machine.Stop();
        rig.Map.Dispose();

        rig.Handles.Live.ShouldBe(0);
        rig.Delivered[^1].IsInactive.ShouldBeTrue();
    }
}
