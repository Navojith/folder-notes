using FolderNotes.App.Explorer;
using Microsoft.Extensions.Time.Testing;

namespace FolderNotes.App.Tests.Explorer;

/// <summary>04 B.6 "Process thread budget" (Q2): 1 worker + 1 hook + 1 watchdog live, ≤ 6 abandoned workers, ≤ 1 abandoned hook thread.</summary>
[Trait("Kind", "Deterministic")]
public class TrackerThreadBudgetTests
{
    [Fact]
    public void NFR06_The_default_budget_is_3_live_and_10_owned_at_most()
    {
        var b = TrackerThreadBudget.Default;
        b.MaxLiveWorkers.ShouldBe(1);
        b.MaxLiveHook.ShouldBe(1);
        b.MaxLiveWatchdog.ShouldBe(1);
        b.MaxLiveTotal.ShouldBe(3);
        b.MaxAbandonedHook.ShouldBe(1);
        TrackerThreadBudget.DefaultMaxAbandonedWorkers.ShouldBe(6);
        b.MaxOwnedAtOnce.ShouldBe(10);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void NFR06_Each_kind_allows_one_live_thread_and_release_frees_the_slot(int kindValue)
    {
        var kind = (TrackerThreadKind)kindValue;
        var g = new TrackerProcessGuard();

        g.TryReserveThread(kind).ShouldBeTrue();
        g.TryReserveThread(kind).ShouldBeFalse();
        g.LiveThreads(kind).ShouldBe(1);

        g.ReleaseThread(kind);
        g.ReleaseThread(kind);                                // double release is harmless
        g.LiveThreads(kind).ShouldBe(0);
        g.TryReserveThread(kind).ShouldBeTrue();
    }

    [Fact]
    public void NFR06_All_three_kinds_can_be_live_together_but_never_more()
    {
        var g = new TrackerProcessGuard();

        g.TryReserveThread(TrackerThreadKind.Worker).ShouldBeTrue();
        g.TryReserveThread(TrackerThreadKind.Hook).ShouldBeTrue();
        g.TryReserveThread(TrackerThreadKind.Watchdog).ShouldBeTrue();

        g.OwnedThreads.ShouldBe(3);
        g.TryReserveThread(TrackerThreadKind.Worker).ShouldBeFalse();
        g.TryReserveThread(TrackerThreadKind.Hook).ShouldBeFalse();
        g.TryReserveThread(TrackerThreadKind.Watchdog).ShouldBeFalse();
    }

    [Fact]
    public void NFR06_Abandoning_frees_the_live_slot_but_still_counts_against_the_process()
    {
        var g = new TrackerProcessGuard();
        g.TryReserveThread(TrackerThreadKind.Worker).ShouldBeTrue();

        g.AbandonThread(TrackerThreadKind.Worker).ShouldBe(1);

        g.LiveThreads(TrackerThreadKind.Worker).ShouldBe(0);
        g.AbandonedThreads.ShouldBe(1);
        g.OwnedThreads.ShouldBe(1);                           // the zombie is still ours
        g.TryReserveThread(TrackerThreadKind.Worker).ShouldBeTrue();
        g.OwnedThreads.ShouldBe(2);
    }

    [Fact]
    public void NFR06_An_abandoned_hook_thread_is_never_replaced_in_this_process()
    {
        var g = new TrackerProcessGuard();
        g.TryReserveThread(TrackerThreadKind.Hook).ShouldBeTrue();

        g.AbandonThread(TrackerThreadKind.Hook).ShouldBe(1);

        g.AbandonedHookThreads.ShouldBe(1);
        g.TryReserveThread(TrackerThreadKind.Hook).ShouldBeFalse();
    }

    [Fact]
    public void NFR06_Slots_are_taken_exactly_once_under_a_race()
    {
        var g = new TrackerProcessGuard();
        int wins = 0;

        Parallel.For(0, 200, _ =>
        {
            if (g.TryReserveThread(TrackerThreadKind.Hook))
            {
                Interlocked.Increment(ref wins);
            }
        });

        wins.ShouldBe(1);
    }

    [Fact]
    public void NFR06_The_state_machine_holds_the_worker_slot_from_Start_to_Stop_and_through_trials()
    {
        var clock = new FakeTimeProvider();
        var g = new TrackerProcessGuard(quarantine: new QuarantineRecord(clock));
        var m = new TrackerStateMachine(clock, new StatePublisher(_ => { }), g);

        m.Start().ShouldBe(StartResult.Started);
        g.LiveThreads(TrackerThreadKind.Worker).ShouldBe(1);

        m.OnWedge(TestEntries.Unknown(new FakeZombie(), clock));   // abandoned: slot freed, zombie counted, no replacement
        g.LiveThreads(TrackerThreadKind.Worker).ShouldBe(0);
        g.OwnedThreads.ShouldBe(1);
        m.Retry().ShouldBe(RetryResult.TrialStarted);         // a trial takes the one slot (04 § M.8)
        g.LiveThreads(TrackerThreadKind.Worker).ShouldBe(1);
        g.OwnedThreads.ShouldBe(2);

        m.Stop();
        g.LiveThreads(TrackerThreadKind.Worker).ShouldBe(0);
        g.AbandonedThreads.ShouldBe(1);                       // Stop never forgives an abandoned worker
    }

    [Fact]
    public void NFR06_Worst_case_the_whole_budget_is_exhausted_at_ten_threads_and_no_more_can_be_made()
    {
        var clock = new FakeTimeProvider();
        var g = new TrackerProcessGuard(quarantine: new QuarantineRecord(clock));
        var m = new TrackerStateMachine(clock, new StatePublisher(_ => { }), g);
        m.Start();
        g.TryReserveThread(TrackerThreadKind.Hook).ShouldBeTrue();
        g.TryReserveThread(TrackerThreadKind.Watchdog).ShouldBeTrue();

        for (int i = 0; i < 6; i++)
        {
            m.OnWedge(TestEntries.Unknown(new FakeZombie(), clock));
            if (m.CanRetry)
            {
                m.Retry().ShouldBe(RetryResult.TrialStarted);   // quarantined or paused: the next worker is a trial
            }

            clock.Advance(TimeSpan.FromMinutes(11));
        }

        g.AbandonThread(TrackerThreadKind.Hook);              // the hook thread wedges too

        g.AbandonedThreads.ShouldBe(6);
        g.AbandonedHookThreads.ShouldBe(1);
        g.OwnedThreads.ShouldBeLessThanOrEqualTo(g.Budget.MaxOwnedAtOnce);
        m.IsPermanentlyDisabled.ShouldBeTrue();
        g.TryReserveThread(TrackerThreadKind.Hook).ShouldBeFalse();
        m.Retry().ShouldBe(RetryResult.ThreadCapReached);
    }
}
