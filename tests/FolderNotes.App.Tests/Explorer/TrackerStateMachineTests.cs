using FolderNotes.App.Explorer;
using Microsoft.Extensions.Time.Testing;

namespace FolderNotes.App.Tests.Explorer;

/// <summary>04 B.6 and F: lifecycle, the breaker, thread limits, abandoned-worker silence, recovery back-off.</summary>
public class TrackerStateMachineTests
{
    private readonly FakeTimeProvider _clock = new();
    private readonly List<ActiveFolderState> _delivered = [];
    private readonly StatePublisher _pub;
    private readonly TrackerProcessGuard _guard = new();

    public TrackerStateMachineTests() => _pub = new StatePublisher(_delivered.Add);

    private TrackerStateMachine Machine(TrackerLimits? limits = null, TrackerProcessGuard? guard = null) =>
        new(_clock, _pub, guard ?? _guard, limits);

    private TrackerStateMachine Started(TrackerLimits? limits = null)
    {
        var m = Machine(limits);
        m.Start().ShouldBe(StartResult.Started);
        return m;
    }

    private void Minutes(double m) => _clock.Advance(TimeSpan.FromMinutes(m));

    // ---- lifecycle -----------------------------------------------------------------------------------

    [Fact]
    public void NFR01_Start_moves_Stopped_to_Running_with_one_live_worker()
    {
        var m = Machine();
        m.State.ShouldBe(TrackerState.Stopped);

        m.Start().ShouldBe(StartResult.Started);

        m.State.ShouldBe(TrackerState.Running);
        m.LiveWorkers.ShouldBe(1);
        m.Start().ShouldBe(StartResult.AlreadyRunning);
    }

    [Fact]
    public void NFR01_Stop_returns_to_Stopped_publishes_Inactive_and_frees_the_process_slot()
    {
        var m = Started();
        _pub.TryPublish(_pub.Generation, _pub.NextSequence(), new ActiveFolderState(1, 1, @"C:\a", 0));

        m.Stop();

        m.State.ShouldBe(TrackerState.Stopped);
        m.LiveWorkers.ShouldBe(0);
        _delivered[^1].IsInactive.ShouldBeTrue();
        Machine().Start().ShouldBe(StartResult.Started);      // the slot is free again
    }

    [Fact]
    public void NFR01_Only_one_tracker_can_be_active_in_a_process()
    {
        var first = Started();
        var second = Machine();

        second.Start().ShouldBe(StartResult.AnotherTrackerActive);
        second.State.ShouldBe(TrackerState.Stopped);
        second.LiveWorkers.ShouldBe(0);

        first.Stop();
        second.Start().ShouldBe(StartResult.Started);
    }

    [Fact]
    public void NFR01_Two_machines_with_separate_guards_do_not_interfere()
    {
        Started();
        Machine(guard: new TrackerProcessGuard()).Start().ShouldBe(StartResult.Started);
    }

    [Fact]
    public void NFR01_The_shared_guard_is_one_process_wide_instance()
    {
        TrackerProcessGuard.Shared.ShouldBeSameAs(TrackerProcessGuard.Shared);
    }

    // ---- wedge: replacement, silence, thread limits --------------------------------------------------

    [Fact]
    public void NFR01_A_wedge_allows_exactly_one_replacement_and_invalidates_the_old_generation_first()
    {
        var m = Started();
        int oldGeneration = _pub.Generation;
        _pub.TryPublish(oldGeneration, _pub.NextSequence(), new ActiveFolderState(1, 1, @"C:\a", 0));

        var d = m.OnWedge();

        d.Outcome.ShouldBe(WedgeOutcome.StartReplacement);
        d.Generation.ShouldBe(oldGeneration + 1);
        _pub.Generation.ShouldBe(d.Generation);
        m.LiveWorkers.ShouldBe(0);                            // the abandoned worker is no longer counted as live
        _delivered[^1].IsInactive.ShouldBeTrue();             // Inactive was published before any replacement exists
        _guard.AbandonedThreads.ShouldBe(1);

        m.ReplacementStarted();
        m.LiveWorkers.ShouldBe(1);
    }

    [Fact]
    public void NFR01_An_abandoned_worker_cannot_publish_after_a_wedge_but_the_replacement_can()
    {
        var m = Started();
        int zombie = _pub.Generation;
        var d = m.OnWedge();
        m.ReplacementStarted();
        int before = _delivered.Count;

        _pub.TryPublish(zombie, _pub.NextSequence(), new ActiveFolderState(1, 1, @"C:\zombie", 0)).ShouldBe(PublishResult.StaleGeneration);
        _pub.TryPublish(d.Generation, _pub.NextSequence(), new ActiveFolderState(1, 1, @"C:\real", 0)).ShouldBe(PublishResult.Published);

        _delivered.Count.ShouldBe(before + 1);
        _delivered[^1].Path.ShouldBe(@"C:\real");
    }

    [Fact]
    public void NFR01_A_second_live_worker_is_refused()
    {
        var m = Started();
        Should.Throw<InvalidOperationException>(() => m.ReplacementStarted());   // the first worker is still live

        m.OnWedge();
        m.ReplacementStarted();
        Should.Throw<InvalidOperationException>(() => m.ReplacementStarted());   // never two
        m.LiveWorkers.ShouldBe(1);
    }

    [Fact]
    public void NFR01_A_wedge_can_only_be_reported_for_the_live_worker_of_a_running_tracker()
    {
        Should.Throw<InvalidOperationException>(() => Machine().OnWedge());
        var m = Started();
        m.OnWedge();                                          // worker gone, replacement not started yet
        Should.Throw<InvalidOperationException>(() => m.OnWedge());
    }

    [Fact]
    public void NFR01_Repeated_failures_cannot_create_unlimited_threads_the_defaults_stop_at_six()
    {
        var m = Started();
        int replacements = 0;

        for (int i = 0; i < 20; i++)
        {
            if (m.State == TrackerState.Disabled)
            {
                break;
            }

            var d = m.OnWedge();
            if (d.Outcome == WedgeOutcome.StartReplacement)
            {
                m.ReplacementStarted();
                replacements++;
            }

            Minutes(30);                                       // spaced out so the breaker window cannot be what stops it
        }

        replacements.ShouldBe(5);
        _guard.AbandonedThreads.ShouldBe(6);                   // the cap holds: never more than 6 leaked threads
        _guard.LiveThreads(TrackerThreadKind.Worker).ShouldBe(0);
        m.State.ShouldBe(TrackerState.Disabled);
        m.DisabledReason.ShouldBe(DisabledReason.ThreadCap);
        m.LiveWorkers.ShouldBe(0);
    }

    [Fact]
    public void NFR01_The_thread_cap_is_process_wide_across_tracker_instances()
    {
        var limits = new TrackerLimits { MaxAbandonedThreads = 2, BreakerWedges = 100 };
        var a = Started(limits);
        a.OnWedge().Outcome.ShouldBe(WedgeOutcome.StartReplacement);
        a.ReplacementStarted();
        a.Stop();                                              // user turns the panel off and on again

        var b = Started(limits);
        b.OnWedge().Outcome.ShouldBe(WedgeOutcome.Disabled);   // 2nd abandoned thread in the process = the cap
        b.DisabledReason.ShouldBe(DisabledReason.ThreadCap);
        _guard.AbandonedThreads.ShouldBe(2);
    }

    // ---- circuit breaker ----------------------------------------------------------------------------

    private static readonly TrackerLimits RoomyCap = new() { MaxAbandonedThreads = 6 };

    private static void Wedge(TrackerStateMachine m)
    {
        if (m.OnWedge().Outcome == WedgeOutcome.StartReplacement)
        {
            m.ReplacementStarted();
        }
    }

    [Fact]
    public void NFR01_Three_wedges_inside_ten_minutes_trip_the_breaker()
    {
        var m = Started(RoomyCap);
        _pub.TryPublish(_pub.Generation, _pub.NextSequence(), new ActiveFolderState(1, 1, @"C:\a", 0));
        Wedge(m);
        Minutes(3);
        Wedge(m);
        Minutes(3);
        m.State.ShouldBe(TrackerState.Running);

        var d = m.OnWedge();

        d.Outcome.ShouldBe(WedgeOutcome.Disabled);
        d.Reason.ShouldBe(DisabledReason.CircuitBreaker);
        m.State.ShouldBe(TrackerState.Disabled);
        m.LiveWorkers.ShouldBe(0);
        _delivered[^1].IsInactive.ShouldBeTrue();
    }

    [Fact]
    public void NFR01_The_breaker_window_boundary_a_wedge_exactly_ten_minutes_old_has_expired()
    {
        var m = Started(RoomyCap);
        Wedge(m);                                               // t = 0
        Minutes(5);
        Wedge(m);                                               // t = 5
        Minutes(5);                                             // t = 10:00 exactly -> the first wedge is 10 min old = expired
        m.OnWedge().Outcome.ShouldBe(WedgeOutcome.StartReplacement);
        m.State.ShouldBe(TrackerState.Running);
    }

    [Fact]
    public void NFR01_One_tick_inside_the_window_still_counts()
    {
        var m = Started(RoomyCap);
        Wedge(m);                                               // t = 0
        Minutes(5);
        Wedge(m);                                               // t = 5
        _clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromTicks(1));   // t = 10:00 - 1 tick
        m.OnWedge().Outcome.ShouldBe(WedgeOutcome.Disabled);
    }

    [Fact]
    public void NFR01_After_ten_quiet_minutes_the_next_wedge_does_not_trip_it()
    {
        var m = Started(RoomyCap);
        Wedge(m);
        Wedge2(m);
        Minutes(10);

        m.OnWedge().Outcome.ShouldBe(WedgeOutcome.StartReplacement);
        m.WedgesInWindow.ShouldBe(1);
    }

    private void Wedge2(TrackerStateMachine m) { Minutes(1); Wedge(m); }

    [Fact]
    public void NFR01_Start_is_refused_while_Disabled_and_Stop_does_not_clear_the_latch()
    {
        var m = Started(RoomyCap);
        Wedge(m); Wedge(m); m.OnWedge();
        m.State.ShouldBe(TrackerState.Disabled);

        m.Start().ShouldBe(StartResult.Disabled);
        m.Stop();                                               // e.g. the panel is switched off
        m.State.ShouldBe(TrackerState.Disabled);
        m.Start().ShouldBe(StartResult.Disabled);               // switching it back on is not a Retry
    }

    [Fact]
    public void NFR01_Retry_after_a_breaker_trip_restarts_with_one_worker_and_a_clean_window()
    {
        var m = Started(RoomyCap);
        Wedge(m); Wedge(m); m.OnWedge();

        m.Retry().ShouldBe(RetryResult.Started);

        m.State.ShouldBe(TrackerState.Running);
        m.LiveWorkers.ShouldBe(1);
        m.DisabledReason.ShouldBe(DisabledReason.None);
        m.WedgesInWindow.ShouldBe(0);
        // it takes three fresh wedges to trip again
        Wedge(m);
        m.State.ShouldBe(TrackerState.Running);
        Wedge(m);
        m.State.ShouldBe(TrackerState.Running);
        m.OnWedge().Outcome.ShouldBe(WedgeOutcome.Disabled);
    }

    [Fact]
    public void NFR01_Retry_is_refused_once_the_lifetime_thread_cap_is_used_up()
    {
        var m = Started(RoomyCap);                              // cap 6
        Wedge(m); Wedge(m); m.OnWedge();                        // 3 abandoned, breaker
        m.Retry().ShouldBe(RetryResult.Started);
        Wedge(m); Wedge(m);                                     // 5 abandoned
        m.OnWedge().Outcome.ShouldBe(WedgeOutcome.Disabled);    // 6th abandoned: breaker AND cap

        m.Retry().ShouldBe(RetryResult.ThreadCapReached);
        m.State.ShouldBe(TrackerState.Disabled);
        _guard.AbandonedThreads.ShouldBe(6);
    }

    // ---- Q1 (04 B.6): breaker 3 wedges / 10 min, lifetime cap 6, defaults ---------------------------------

    [Fact]
    public void NFR01_Q1_The_shipped_defaults_are_breaker_3_in_10_minutes_and_a_lifetime_cap_of_6()
    {
        TrackerLimits.Default.BreakerWedges.ShouldBe(3);
        TrackerLimits.Default.BreakerWindow.ShouldBe(TimeSpan.FromMinutes(10));
        TrackerLimits.Default.MaxAbandonedThreads.ShouldBe(6);
    }

    [Fact]
    public void NFR01_Q1_With_the_defaults_the_first_breaker_trip_accepts_one_Retry()
    {
        var m = Started();
        Wedge(m); Wedge(m);
        var d = m.OnWedge();

        d.Reason.ShouldBe(DisabledReason.CircuitBreaker);      // 3 abandoned: retryable, not permanent
        m.IsPermanentlyDisabled.ShouldBeFalse();
        m.CanRetry.ShouldBeTrue();
        m.Retry().ShouldBe(RetryResult.Started);
        _guard.AbandonedThreads.ShouldBe(3);                   // Retry never resets the lifetime counter
    }

    [Fact]
    public void NFR01_Q1_Second_trip_exhausts_the_cap_and_the_permanent_reason_wins()
    {
        var m = Started();
        Wedge(m); Wedge(m); m.OnWedge();                       // episode 1: 3 abandoned
        m.Retry().ShouldBe(RetryResult.Started);
        Wedge(m); Wedge(m);                                    // 5 abandoned
        var d = m.OnWedge();                                   // 6th: breaker AND cap at the same time

        d.Outcome.ShouldBe(WedgeOutcome.Disabled);
        d.Reason.ShouldBe(DisabledReason.ThreadCap);
        m.DisabledReason.ShouldBe(DisabledReason.ThreadCap);
        m.IsPermanentlyDisabled.ShouldBeTrue();
        m.CanRetry.ShouldBeFalse();
        m.Retry().ShouldBe(RetryResult.ThreadCapReached);
        _guard.AbandonedThreads.ShouldBe(6);
    }

    [Fact]
    public void NFR01_Q1_Permanent_exhaustion_is_never_lifted_by_waiting_restarting_or_stopping()
    {
        var m = Started();
        Wedge(m); Wedge(m); m.OnWedge();
        m.Retry();
        Wedge(m); Wedge(m); m.OnWedge();

        Minutes(24 * 60);                                      // a day later
        m.Retry().ShouldBe(RetryResult.ThreadCapReached);
        m.Stop();
        m.Start().ShouldBe(StartResult.Disabled);
        m.Retry().ShouldBe(RetryResult.ThreadCapReached);
        m.IsPermanentlyDisabled.ShouldBeTrue();
        Machine().Start().ShouldBe(StartResult.Disabled);   // a new tracker object cannot reset the count: the process is exhausted
        _guard.AbandonedThreads.ShouldBe(6);
    }

    [Fact]
    public void NFR01_Q1_The_cap_can_be_reached_without_ever_tripping_the_breaker_and_is_still_permanent()
    {
        var m = Started();
        for (int i = 0; i < 5; i++)
        {
            Wedge(m);
            Minutes(11);                                       // each wedge expires before the next: breaker never trips
        }

        var d = m.OnWedge();

        d.Reason.ShouldBe(DisabledReason.ThreadCap);
        m.IsPermanentlyDisabled.ShouldBeTrue();
        m.WedgesInWindow.ShouldBe(1);
    }

    [Fact]
    public void NFR01_Q1_The_exhaustion_count_is_shared_by_every_tracker_instance_of_the_process()
    {
        var a = Started();
        Wedge(a); Wedge(a); a.OnWedge();                       // 3 abandoned by tracker A, breaker
        a.Stop();                                              // stays Disabled and keeps the slot
        a.Retry().ShouldBe(RetryResult.Started);
        Wedge(a); Wedge(a);
        a.OnWedge().Reason.ShouldBe(DisabledReason.ThreadCap);

        var b = Machine();
        b.Start().ShouldBe(StartResult.Disabled);              // the shared count says the process is exhausted
        _guard.AbandonedThreads.ShouldBe(6);
    }

    [Fact]
    public void NFR01_Stop_with_an_abandoned_worker_counts_it_against_the_lifetime_cap_but_not_the_breaker()
    {
        var m = Started();

        m.Stop(workerAbandoned: true);

        m.State.ShouldBe(TrackerState.Stopped);
        _guard.AbandonedThreads.ShouldBe(1);
        _guard.LiveThreads(TrackerThreadKind.Worker).ShouldBe(0);
        m.WedgesInWindow.ShouldBe(0);                          // a stuck Stop is not an evaluation wedge
    }

    [Fact]
    public void NFR01_Start_is_refused_once_stops_alone_used_up_the_lifetime_cap()
    {
        var limits = new TrackerLimits { MaxAbandonedThreads = 2 };
        for (int i = 0; i < 2; i++)
        {
            var m = Started(limits);
            m.Stop(workerAbandoned: true);
        }

        Machine(limits).Start().ShouldBe(StartResult.Disabled);
        _guard.AbandonedThreads.ShouldBe(2);
    }

    [Fact]
    public void NFR01_Retry_outside_Disabled_does_nothing()
    {
        Machine().Retry().ShouldBe(RetryResult.NotDisabled);
        Started().Retry().ShouldBe(RetryResult.NotDisabled);
    }

    [Fact]
    public void NFR01_A_disabled_tracker_keeps_the_process_slot_so_no_second_tracker_can_replace_it()
    {
        var m = Started(RoomyCap);
        Wedge(m); Wedge(m); m.OnWedge();
        m.Stop();

        Machine().Start().ShouldBe(StartResult.AnotherTrackerActive);
        m.Retry().ShouldBe(RetryResult.Started);                // the owner itself may retry
    }

    [Fact]
    public void NFR01_The_limits_must_be_positive()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => Machine(new TrackerLimits { MaxAbandonedThreads = 0 }));
        Should.Throw<ArgumentOutOfRangeException>(() => Machine(new TrackerLimits { BreakerWedges = 0 }));
    }

    // ---- recovery ----------------------------------------------------------------------------------

    [Fact]
    public void REQ031_ShellGone_publishes_Inactive_and_enters_Recovering()
    {
        var m = Started();
        _pub.TryPublish(_pub.Generation, _pub.NextSequence(), new ActiveFolderState(1, 1, @"C:\a", 0));

        m.ShellGone();

        m.State.ShouldBe(TrackerState.Recovering);
        _delivered[^1].IsInactive.ShouldBeTrue();
        m.LiveWorkers.ShouldBe(1);                              // the same worker keeps retrying; no new thread
    }

    [Fact]
    public void REQ031_ShellGone_is_idempotent_and_ignored_when_not_running()
    {
        var m = Started();
        m.ShellGone();
        int n = _delivered.Count;
        m.ShellGone();
        _delivered.Count.ShouldBe(n);
        Machine(guard: new TrackerProcessGuard()).ShellGone();   // a stopped tracker: no effect, no throw
    }

    [Fact]
    public void REQ031_Recovery_back_off_is_250_500_1000_2000_then_5s_forever()
    {
        var m = Started();
        m.ShellGone();

        var delays = Enumerable.Range(0, 9).Select(_ => m.NextRecoveryDelay().TotalMilliseconds).ToList();

        delays.ShouldBe([250, 500, 1000, 2000, 5000, 5000, 5000, 5000, 5000]);
    }

    [Fact]
    public void REQ031_Recovery_success_returns_to_Running_and_restarts_the_back_off()
    {
        var m = Started();
        m.ShellGone();
        m.NextRecoveryDelay(); m.NextRecoveryDelay();

        m.RecoverySucceeded();

        m.State.ShouldBe(TrackerState.Running);
        m.ShellGone();
        m.NextRecoveryDelay().ShouldBe(TimeSpan.FromMilliseconds(250));
    }

    [Fact]
    public void REQ031_A_wedge_while_Recovering_is_handled_like_any_wedge()
    {
        var m = Started();
        m.ShellGone();

        m.OnWedge().Outcome.ShouldBe(WedgeOutcome.StartReplacement);
        m.ReplacementStarted();
        m.State.ShouldBe(TrackerState.Recovering);
    }

    // ---- root validation (04 F, L7) -------------------------------------------------------------------

    [Fact]
    public void REQ031_Two_empty_enumerations_while_Explorer_frames_exist_mean_the_root_is_wrong()
    {
        var v = new RootValidator();
        v.Observe(0, explorerFramesExist: true).ShouldBe(RootVerdict.Suspect);
        v.Observe(0, explorerFramesExist: true).ShouldBe(RootVerdict.ReplaceRoot);
    }

    [Fact]
    public void REQ031_An_empty_enumeration_with_no_Explorer_frames_is_fine()
    {
        var v = new RootValidator();
        v.Observe(0, explorerFramesExist: false).ShouldBe(RootVerdict.Ok);
        v.Observe(0, explorerFramesExist: false).ShouldBe(RootVerdict.Ok);
    }

    [Fact]
    public void REQ031_A_non_empty_enumeration_clears_the_suspicion()
    {
        var v = new RootValidator();
        v.Observe(0, true).ShouldBe(RootVerdict.Suspect);
        v.Observe(3, true).ShouldBe(RootVerdict.Ok);
        v.Observe(0, true).ShouldBe(RootVerdict.Suspect);       // counting starts over
    }

    [Fact]
    public void REQ031_After_ReplaceRoot_the_count_restarts_for_the_new_root()
    {
        var v = new RootValidator();
        v.Observe(0, true); v.Observe(0, true).ShouldBe(RootVerdict.ReplaceRoot);
        v.Observe(0, true).ShouldBe(RootVerdict.Suspect);
    }
}
