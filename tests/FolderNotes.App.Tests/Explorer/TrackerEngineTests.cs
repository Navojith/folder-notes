using FolderNotes.App.Explorer;
using Microsoft.Extensions.Time.Testing;

namespace FolderNotes.App.Tests.Explorer;

internal sealed class FakeScope : IHookScope
{
    public List<uint> Requests { get; } = [];

    public bool SetScope(uint processId)
    {
        Requests.Add(processId);
        return true;
    }
}

/// <summary>The engine wired to a fake desktop and a fake clock: no threads, no COM, deterministic.</summary>
internal sealed class EngineRig : IDisposable
{
    public FakeTimeProvider Clock { get; } = new();

    public FakeExplorerDesktop Desk { get; } = new();

    public List<ActiveFolderState> Delivered { get; } = [];

    public StatePublisher Publisher { get; }

    public TrackerProcessGuard Guard { get; } = new();

    public TrackerStateMachine Machine { get; }

    public FakeScope Scope { get; } = new();

    public TrackerCounters Counters { get; } = new();

    public TrackerEngine Engine { get; private set; }

    public List<string> Log { get; } = [];

    public EngineRig(int? generationOverride = null)
    {
        Publisher = new StatePublisher(Delivered.Add);
        Machine = new TrackerStateMachine(Clock, Publisher, Guard);
        Machine.Start();
        Engine = NewEngine(generationOverride ?? Publisher.Generation);
    }

    public TrackerEngine NewEngine(int generation) =>
        new(generation, Desk, Desk, Publisher, Machine, Scope, Counters, Clock, null, Log.Add);

    /// <summary>Runs the loop for <paramref name="ms"/> of fake time, one millisecond per step.</summary>
    public void Pump(int ms)
    {
        for (int t = 0; t < ms; t++)
        {
            for (int guard = 0; guard < 100; guard++)
            {
                var next = Engine.Tick();
                if (next is not { } d || d > TimeSpan.Zero)
                {
                    break;
                }
            }

            Clock.Advance(TimeSpan.FromMilliseconds(1));
        }
    }

    public void Signal(TrackerSignal bits = TrackerSignal.NameChange) => Engine.OnSignals(bits);

    public void OneFrame(string path = @"C:\a", nint frame = 1, nint key = 11, uint pid = 500)
    {
        Desk.AddTab(frame, key, path, true, pid);
        Desk.Foreground = frame;
    }

    public void Dispose() => Engine.Dispose();
}

/// <summary>04 L: the evaluation, hook-scope policy and recovery orchestration, against a fake Explorer on a fake clock.</summary>
public sealed class TrackerEngineTests : IDisposable
{
    private readonly EngineRig _r = new();

    public void Dispose() => _r.Dispose();

    // ---- evaluation (REQ-031, REQ-034) -----------------------------------------------------------------

    [Fact]
    public void REQ031_Start_connects_and_the_first_evaluation_publishes_the_foreground_folder()
    {
        _r.OneFrame(@"C:\a");

        _r.Engine.Start();
        _r.Pump(5);

        _r.Engine.Connected.ShouldBeTrue();
        _r.Delivered.ShouldHaveSingleItem().Path.ShouldBe(@"C:\a");
        _r.Delivered[0].FrameHwnd.ShouldBe((nint)1);
        _r.Delivered[0].TabHwnd.ShouldBe((nint)11);
        _r.Machine.State.ShouldBe(TrackerState.Running);
    }

    [Fact]
    public void REQ031_Navigation_is_published_within_the_budget_and_unchanged_state_is_not_redelivered()
    {
        _r.OneFrame();
        _r.Engine.Start();
        _r.Pump(5);
        _r.Clock.Advance(TimeSpan.FromMilliseconds(100));

        _r.Desk.Navigate(1, @"C:\b");
        var before = _r.Clock.GetUtcNow();
        _r.Signal();
        _r.Pump(2);

        _r.Delivered[^1].Path.ShouldBe(@"C:\b");
        (_r.Clock.GetUtcNow() - before).TotalMilliseconds.ShouldBeLessThan(150);
        int count = _r.Delivered.Count;
        for (int i = 0; i < 100; i++)
        {
            _r.Signal();
            _r.Pump(7);
        }

        _r.Pump(15_000);                                       // polls and reconciles too
        _r.Delivered.Count.ShouldBe(count);
    }

    [Fact]
    public void REQ031_Rapid_navigation_ends_on_the_final_folder_with_strictly_increasing_sequences_and_bounded_work()
    {
        _r.OneFrame();
        _r.Engine.Start();
        _r.Pump(5);
        long evalsBefore = _r.Counters.Evaluations;

        for (int i = 0; i < 200; i++)
        {
            _r.Desk.Navigate(1, $@"C:\f{i}");
            _r.Signal();
            _r.Pump(1);
        }

        _r.Pump(200);

        _r.Delivered[^1].Path.ShouldBe(@"C:\f199");
        (_r.Counters.Evaluations - evalsBefore).ShouldBeLessThan(200 / 30 + 12);
        _r.Delivered.Select(d => d.Sequence).ShouldBe(_r.Delivered.Select(d => d.Sequence).OrderBy(x => x));
    }

    [Fact]
    public void REQ034_Tab_switch_changes_the_published_tab_only_when_the_folder_differs()
    {
        _r.OneFrame(@"C:\same");
        _r.Desk.AddTab(1, 12, @"C:\same", false);
        _r.Desk.AddTab(1, 13, @"C:\other", false);
        _r.Engine.Start();
        _r.Pump(10);

        _r.Desk.SwitchTab(1, 12); _r.Signal(); _r.Pump(60);
        _r.Delivered.Count.ShouldBe(1);

        _r.Desk.SwitchTab(1, 13); _r.Signal(); _r.Pump(60);
        _r.Delivered.Count.ShouldBe(2);
        _r.Delivered[^1].Path.ShouldBe(@"C:\other");
        _r.Delivered[^1].TabHwnd.ShouldBe((nint)13);
    }

    [Fact]
    public void REQ034_A_new_tab_is_a_cache_miss_that_rebuilds_once_and_resolves()
    {
        _r.OneFrame();
        _r.Engine.Start();
        _r.Pump(10);
        long rebuilds = _r.Counters.Rebuilds;

        _r.Desk.AddTab(1, 12, @"C:\new", false);
        _r.Desk.SwitchTab(1, 12);
        _r.Signal(TrackerSignal.CreateDestroy | TrackerSignal.ShowHide);
        _r.Pump(60);

        (_r.Counters.Rebuilds - rebuilds).ShouldBe(1);
        _r.Delivered[^1].Path.ShouldBe(@"C:\new");
    }

    [Fact]
    public void REQ031_A_frame_without_tab_windows_is_its_own_implicit_tab()
    {
        _r.Desk.AddTab(frame: 7, key: 7, path: @"C:\win10", visible: true);
        _r.Desk.Foreground = 7;
        _r.Engine.Start();
        _r.Pump(10);

        _r.Delivered.ShouldHaveSingleItem().Path.ShouldBe(@"C:\win10");
        _r.Delivered[0].TabHwnd.ShouldBe((nint)7);
    }

    [Fact]
    public void REQ031_A_virtual_folder_publishes_a_state_with_no_path()
    {
        _r.OneFrame(path: null!);

        _r.Engine.Start();
        _r.Pump(10);

        _r.Delivered.ShouldHaveSingleItem().Path.ShouldBeNull();
        _r.Delivered[0].IsInactive.ShouldBeFalse();
    }

    [Fact]
    public void REQ038_A_window_owned_by_the_frame_keeps_the_folder_and_another_app_makes_it_inactive()
    {
        _r.OneFrame(@"C:\a");
        _r.Desk.SetOwner(window: 900, owner: 1);                // our docked panel is owned by the frame
        _r.Desk.AddForeignWindow(2000);
        _r.Engine.Start();
        _r.Pump(10);

        _r.Desk.Foreground = 900; _r.Signal(TrackerSignal.Foreground); _r.Pump(60);
        _r.Delivered[^1].Path.ShouldBe(@"C:\a");
        _r.Delivered.Count.ShouldBe(1);

        _r.Desk.Foreground = 2000; _r.Signal(TrackerSignal.Foreground); _r.Pump(60);
        _r.Delivered[^1].IsInactive.ShouldBeTrue();
    }

    [Fact]
    public void REQ036_A_minimized_frame_is_inactive()
    {
        _r.OneFrame();
        _r.Engine.Start();
        _r.Pump(10);

        _r.Desk.Minimize(1);
        _r.Signal(TrackerSignal.Minimize);
        _r.Pump(60);

        _r.Delivered[^1].IsInactive.ShouldBeTrue();
    }

    [Fact]
    public void NFR06_With_another_app_in_the_foreground_nothing_is_scheduled()
    {
        _r.OneFrame();
        _r.Desk.AddForeignWindow(2000);
        _r.Engine.Start();
        _r.Pump(10);
        _r.Desk.Foreground = 2000; _r.Signal(TrackerSignal.Foreground); _r.Pump(60);
        long evals = _r.Counters.Evaluations;

        _r.Pump(60_000);                                        // a minute of silence

        _r.Counters.Evaluations.ShouldBe(evals);               // no poll, no reconcile, no retry (idle CPU 0)
        _r.Engine.Tick().ShouldBeNull();
    }

    // ---- hook-scope policy -------------------------------------------------------------------------------

    [Fact]
    public void NFR06_The_hook_scope_follows_the_foreground_Explorer_process_and_is_not_requested_twice()
    {
        _r.OneFrame(pid: 500);
        _r.Desk.AddTab(2, 21, @"C:\two", true, pid: 777);
        _r.Desk.AddForeignWindow(2000);
        _r.Engine.Start();
        _r.Pump(10);
        _r.Scope.Requests.ShouldBe([500u]);

        for (int i = 0; i < 20; i++) { _r.Signal(); _r.Pump(40); }
        _r.Scope.Requests.ShouldBe([500u]);                     // same process: no further request

        _r.Desk.Foreground = 2; _r.Signal(TrackerSignal.Foreground); _r.Pump(60);
        _r.Scope.Requests.ShouldBe([500u, 777u]);               // a separate-process Explorer window re-scopes

        _r.Desk.Foreground = 2000; _r.Signal(TrackerSignal.Foreground); _r.Pump(60);
        _r.Scope.Requests.ShouldBe([500u, 777u, 0u]);           // no Explorer frame foreground: scoped hooks off
    }

    // ---- failures and ladder -----------------------------------------------------------------------------

    [Fact]
    public void REQ031_A_transient_failure_is_retried_on_the_ladder_and_publishes_nothing_wrong_meanwhile()
    {
        _r.OneFrame(@"C:\a");
        _r.Engine.Start();
        _r.Pump(10);
        _r.Desk.Navigate(1, @"C:\b");
        _r.Desk.FailNextReads(unchecked((int)0x8001010A));      // RPC_E_SERVERCALL_RETRYLATER

        _r.Signal();
        _r.Pump(200);

        _r.Delivered.Select(d => d.Path).ShouldBe([@"C:\a", @"C:\b"]);   // no Inactive or null flicker in between
        _r.Engine.TabEntries.ShouldBe(1);
    }

    [Fact]
    public void REQ031_Three_Other_failures_drop_the_entry_and_the_next_miss_rebuilds_it()
    {
        _r.OneFrame(@"C:\a");
        _r.Engine.Start();
        _r.Pump(10);
        long rebuilds = _r.Counters.Rebuilds;
        int e = unchecked((int)0x8000FFFF);                      // E_UNEXPECTED
        _r.Desk.FailNextReads(e, e, e);

        for (int i = 0; i < 3; i++) { _r.Signal(); _r.Pump(60); }
        _r.Signal(); _r.Pump(200);

        _r.Counters.Rebuilds.ShouldBeGreaterThan(rebuilds);
        _r.Engine.TabEntries.ShouldBe(1);
        _r.Delivered[^1].Path.ShouldBe(@"C:\a");
    }

    [Fact]
    public void NFR01_An_exception_in_the_gateway_never_escapes_Tick_and_the_next_evaluation_works()
    {
        _r.OneFrame(@"C:\a");
        _r.Engine.Start();
        _r.Pump(10);
        _r.Desk.Navigate(1, @"C:\b");
        _r.Desk.ThrowOnNextRead = true;

        _r.Signal();
        Should.NotThrow(() => _r.Pump(300));

        _r.Log.ShouldContain(m => m.Contains("evaluation failed", StringComparison.OrdinalIgnoreCase));
        _r.Delivered[^1].Path.ShouldBe(@"C:\b");               // the ladder re-read after the failure
    }

    [Fact]
    public void NFR01_Tick_does_at_most_a_bounded_number_of_evaluations_per_wake_up()
    {
        _r.OneFrame();
        _r.Engine.Start();
        _r.Pump(10);
        _r.Desk.FailNextReads(Enumerable.Repeat(unchecked((int)0x8001010A), 50).ToArray());

        long before = _r.Counters.Evaluations;
        _r.Signal();
        _r.Clock.Advance(TimeSpan.FromSeconds(30));             // everything is overdue at once
        _r.Engine.Tick();

        (_r.Counters.Evaluations - before).ShouldBeLessThanOrEqualTo(TrackerEngine.MaxStepsPerTick);
    }

    // ---- unresolved frame, elevated Explorer (04 G, L8) ----------------------------------------------------

    [Fact]
    public void NFR01_L8_A_frame_that_cannot_be_read_is_not_retried_in_a_loop_but_a_signal_tries_again()
    {
        _r.OneFrame(@"C:\admin");
        _r.Desk.HideFromRoot(11);                               // exists as a window, invisible to our root
        _r.Engine.Start();
        _r.Pump(11_000);                                        // L7 replaces the "empty" root twice, then accepts it; then the full ladder runs
        _r.Desk.GatewaysCreated.ShouldBeLessThanOrEqualTo(2 * TrackerEngine.MaxRootReplacements + 2);   // two empty looks per replacement, then accepted: bounded, not a loop
        _r.Engine.Connected.ShouldBeTrue();
        _r.Engine.FrameMarkedUnresolved.ShouldBeTrue();
        _r.Delivered.ShouldBeEmpty();                           // no path, no crash
        long rebuilds = _r.Counters.Rebuilds;
        long evals = _r.Counters.Evaluations;

        _r.Pump(60_000);                                        // a minute: polls and reconciles must not re-read it

        _r.Counters.Rebuilds.ShouldBe(rebuilds);
        _r.Machine.State.ShouldBe(TrackerState.Running);
        (_r.Counters.Evaluations - evals).ShouldBeLessThan(200);   // skipped polls are cheap no-ops, not enumerations

        _r.Signal(TrackerSignal.Foreground);                    // the user focuses it again: one fresh attempt
        _r.Pump(10);
        _r.Counters.Rebuilds.ShouldBeGreaterThan(rebuilds);
    }

    // ---- recovery (04 F) -----------------------------------------------------------------------------------

    [Fact]
    public void NFR01_F_A_dead_root_publishes_Inactive_first_then_recovers_by_itself_when_the_shell_is_back()
    {
        _r.OneFrame(@"C:\a");
        _r.Engine.Start();
        _r.Pump(10);
        _r.Delivered.Count.ShouldBe(1);

        _r.Desk.ShellWindow = 0;                                 // Explorer died
        _r.Desk.EnumerateHr = unchecked((int)0x80010108);       // RPC_E_DISCONNECTED
        _r.Desk.Foreground = 1;
        _r.Desk.AddForeignWindow(2000);
        _r.Signal(TrackerSignal.CreateDestroy);
        _r.Desk.CloseTab(11);                                    // a rebuild is forced by a miss
        _r.Desk.AddTab(1, 12, @"C:\a", true);
        _r.Signal(TrackerSignal.Foreground);
        _r.Pump(100);

        _r.Machine.State.ShouldBe(TrackerState.Recovering);
        _r.Delivered[^1].IsInactive.ShouldBeTrue();
        _r.Engine.Connected.ShouldBeFalse();
        _r.Desk.LiveHandles.ShouldBe(0);                         // released on the worker, all of them
        _r.Desk.LiveGateways.ShouldBe(0);
        _r.Desk.GatewaysCreated.ShouldBe(1);

        _r.Pump(10_000);                                         // shell still down: attempts back off but never create ShellWindows
        _r.Desk.CreateAttemptsWhileShellDown.ShouldBe(0);
        _r.Desk.GatewaysCreated.ShouldBe(1);

        _r.Desk.ShellWindow = 1000;                              // the shell returns, with a new process
        _r.Desk.ShellPid = 900;
        _r.Desk.EnumerateHr = 0;
        _r.Pump(6_000);

        _r.Machine.State.ShouldBe(TrackerState.Running);
        _r.Engine.Connected.ShouldBeTrue();
        _r.Delivered[^1].Path.ShouldBe(@"C:\a");                 // published again with no user action
        _r.Desk.GatewaysCreated.ShouldBe(2);
        _r.Counters.Recoveries.ShouldBe(1);
    }

    [Fact]
    public void NFR01_F_A_changed_shell_process_is_detected_without_any_COM_failure()
    {
        _r.OneFrame(@"C:\a");
        _r.Engine.Start();
        _r.Pump(10);

        _r.Desk.ShellPid = 901;                                  // explorer.exe restarted between two evaluations
        _r.Signal();
        _r.Pump(60);

        _r.Machine.State.ShouldBe(TrackerState.Recovering);
        _r.Delivered[^1].IsInactive.ShouldBeTrue();
        _r.Pump(2_000);
        _r.Machine.State.ShouldBe(TrackerState.Running);
        _r.Delivered[^1].Path.ShouldBe(@"C:\a");
    }

    [Fact]
    public void NFR01_F_NotifyShellRestarted_rebuilds_everything_even_when_nothing_looks_wrong()
    {
        _r.OneFrame(@"C:\a");
        _r.Engine.Start();
        _r.Pump(10);

        _r.Engine.OnShellRestarted();
        _r.Machine.State.ShouldBe(TrackerState.Recovering);
        _r.Delivered[^1].IsInactive.ShouldBeTrue();
        _r.Pump(1_000);

        _r.Machine.State.ShouldBe(TrackerState.Running);
        _r.Delivered[^1].Path.ShouldBe(@"C:\a");
        _r.Desk.GatewaysCreated.ShouldBe(2);
        _r.Desk.LiveGateways.ShouldBe(1);
    }

    [Fact]
    public void NFR01_F_A_failing_root_creation_is_retried_on_the_back_off_ladder_without_a_limit()
    {
        _r.OneFrame(@"C:\a");
        for (int i = 0; i < 6; i++) { _r.Desk.CreateFailures.Enqueue(unchecked((int)0x80004005)); }

        _r.Engine.Start();                                       // creation fails: starts in recovery
        _r.Machine.State.ShouldBe(TrackerState.Recovering);
        _r.Pump(250 + 500 + 1000 + 2000 + 5000 + 5000 + 500);

        _r.Machine.State.ShouldBe(TrackerState.Running);
        _r.Delivered[^1].Path.ShouldBe(@"C:\a");
        _r.Desk.CreateFailures.ShouldBeEmpty();
    }

    [Fact]
    public void NFR01_L7_A_private_empty_root_is_replaced_not_trusted()
    {
        _r.OneFrame(@"C:\a");
        _r.Desk.RootIsEmpty = true;                              // CLSID_ShellWindows handed us an empty rundll32 server

        _r.Engine.Start();
        _r.Machine.State.ShouldBe(TrackerState.Recovering);      // Suspect on the first look: not accepted
        _r.Pump(400);
        _r.Machine.State.ShouldBe(TrackerState.Recovering);      // second empty enumeration while frames exist: ReplaceRoot
        int created = _r.Desk.GatewaysCreated;
        created.ShouldBeGreaterThanOrEqualTo(2);
        _r.Desk.LiveGateways.ShouldBe(0);                        // every rejected root was released

        _r.Desk.RootIsEmpty = false;                             // the real server answers
        _r.Pump(7_000);

        _r.Machine.State.ShouldBe(TrackerState.Running);
        _r.Delivered[^1].Path.ShouldBe(@"C:\a");
    }

    // ---- ownership and stale generations -----------------------------------------------------------------

    [Fact]
    public void NFR01_Dispose_releases_every_handle_and_the_root_and_window_churn_does_not_grow_the_cache()
    {
        _r.OneFrame();
        _r.Engine.Start();
        _r.Pump(10);

        for (int i = 0; i < 50; i++)
        {
            nint key = 100 + i;
            _r.Desk.AddTab(1, key, $@"C:\t{i}", false);
            _r.Desk.SwitchTab(1, key);
            _r.Signal(TrackerSignal.CreateDestroy | TrackerSignal.ShowHide);
            _r.Pump(80);
            _r.Desk.CloseTab(key);
            _r.Desk.SwitchTab(1, 11);
            _r.Signal(TrackerSignal.CreateDestroy | TrackerSignal.ShowHide);
            _r.Pump(80);
        }

        _r.Engine.TabEntries.ShouldBeLessThanOrEqualTo(2);       // returns to the baseline: nothing grows with windows ever seen
        _r.Desk.LiveHandles.ShouldBe(_r.Engine.TabEntries);
        _r.Engine.Dispose();
        _r.Desk.LiveHandles.ShouldBe(0);
        _r.Desk.LiveGateways.ShouldBe(0);
    }

    [Fact]
    public void NFR01_B6_An_abandoned_generation_cannot_re_scope_the_hooks()
    {
        var inner = new FakeScope();
        var scope = new GenerationScope(_r.Publisher.Generation, _r.Publisher, inner);

        scope.SetScope(500).ShouldBeTrue();
        _r.Machine.OnWedge();                                    // the generation moves on
        scope.SetScope(777).ShouldBeFalse();

        inner.Requests.ShouldBe([500u]);
    }

    [Fact]
    public void NFR01_B6_An_engine_of_an_abandoned_generation_cannot_publish_even_if_it_keeps_running()
    {
        _r.OneFrame(@"C:\a");
        _r.Engine.Start();
        _r.Pump(10);
        _r.Delivered.Count.ShouldBe(1);
        var zombie = _r.Engine;

        _r.Machine.OnWedge();                                    // the watchdog abandoned it: generation bumped, Inactive published
        _r.Machine.ReplacementStarted();
        int afterWedge = _r.Delivered.Count;
        _r.Desk.Navigate(1, @"C:\zombie");
        zombie.OnSignals(TrackerSignal.NameChange);
        for (int i = 0; i < 100; i++) { zombie.Tick(); _r.Clock.Advance(TimeSpan.FromMilliseconds(5)); }

        _r.Delivered.Count.ShouldBe(afterWedge);                 // nothing from the old generation, however long it runs
        _r.Delivered.ShouldNotContain(d => d.Path == @"C:\zombie");

        var replacement = _r.NewEngine(_r.Publisher.Generation);
        replacement.Start();
        for (int i = 0; i < 20; i++) { replacement.Tick(); _r.Clock.Advance(TimeSpan.FromMilliseconds(5)); }
        _r.Delivered[^1].Path.ShouldBe(@"C:\zombie");            // the replacement publishes the truth
        replacement.Dispose();
    }
}
