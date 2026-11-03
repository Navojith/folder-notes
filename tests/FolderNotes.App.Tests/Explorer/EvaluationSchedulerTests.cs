using FolderNotes.App.Explorer;
using Microsoft.Extensions.Time.Testing;

namespace FolderNotes.App.Tests.Explorer;

/// <summary>Drives a scheduler like the tracker loop does, on a fake clock.</summary>
internal sealed class SchedulerRig
{
    public FakeTimeProvider Clock { get; } = new();

    public EvaluationScheduler Scheduler { get; }

    public List<(EvaluationRequest Request, TimeSpan At)> Taken { get; } = [];

    private long _seq;
    private readonly DateTimeOffset _start;

    public SchedulerRig(SchedulerOptions? options = null)
    {
        Scheduler = new EvaluationScheduler(Clock, options, () => ++_seq);
        _start = Clock.GetUtcNow();
    }

    public TimeSpan Elapsed => Clock.GetUtcNow() - _start;

    public void Advance(double ms) => Clock.Advance(TimeSpan.FromMilliseconds(ms));

    public EvaluationRequest Take()
    {
        Scheduler.TryTakeDue(out var r).ShouldBeTrue("an evaluation should be due");
        Taken.Add((r, Elapsed));
        return r;
    }

    public CompletionAdvice Done(EvaluationRequest r, bool foreground = true, bool changed = false, EvaluationResultKind kind = EvaluationResultKind.Resolved) =>
        Scheduler.Complete(r.Sequence, new EvaluationOutcome(foreground, changed, kind));

    /// <summary>Takes and completes the next due evaluation with the given outcome.</summary>
    public EvaluationRequest TakeAndComplete(bool foreground = true, bool changed = false, EvaluationResultKind kind = EvaluationResultKind.Resolved)
    {
        var r = Take();
        Done(r, foreground, changed, kind);
        return r;
    }

    public double? UntilMs => Scheduler.TimeUntilNextDue() is { } t ? t.TotalMilliseconds : null;

    /// <summary>Runs the loop for <paramref name="ms"/> in 1 ms steps; each due evaluation costs <paramref name="costMs"/>.</summary>
    public void Run(double ms, Func<EvaluationRequest, EvaluationOutcome> evaluate, double costMs = 0)
    {
        for (double t = 0; t < ms; t += 1)
        {
            while (Scheduler.TryTakeDue(out var r))
            {
                Taken.Add((r, Elapsed));
                if (costMs > 0)
                {
                    Advance(costMs);
                }

                Scheduler.Complete(r.Sequence, evaluate(r));
            }

            Advance(1);
        }
    }
}

/// <summary>04 § ExplorerTracker design D: coalescing, confirm, retry ladder, poll, reconcile (REQ-031, NFR-06).</summary>
[Trait("Kind", "Deterministic")]
public class EvaluationSchedulerTests
{
    private static readonly EvaluationOutcome Fg = new(true, false, EvaluationResultKind.Resolved);

    [Fact]
    public void REQ031_Nothing_is_due_until_a_signal_arrives()
    {
        var rig = new SchedulerRig();
        rig.UntilMs.ShouldBeNull();
        rig.Scheduler.TryTakeDue(out _).ShouldBeFalse();
    }

    [Fact]
    public void REQ031_The_first_signal_after_quiet_is_due_at_once_the_leading_edge()
    {
        var rig = new SchedulerRig();
        rig.Scheduler.Signal(SignalKinds.Navigation);

        rig.UntilMs.ShouldBe(0);
        var r = rig.Take();
        r.Reason.ShouldBe(EvaluationReason.Signal);
        r.Signals.ShouldBe(SignalKinds.Navigation);
    }

    [Fact]
    public void REQ031_Signals_inside_the_window_give_exactly_one_trailing_evaluation_at_its_end()
    {
        var rig = new SchedulerRig();
        rig.Scheduler.Signal(SignalKinds.Navigation);
        var first = rig.Take();                       // window = 30 ms from now
        rig.Done(first, changed: false);

        rig.Advance(10);
        rig.Scheduler.Signal(SignalKinds.Navigation);
        rig.Advance(5);
        rig.Scheduler.Signal(SignalKinds.Foreground);
        rig.Scheduler.Signal(SignalKinds.Navigation);

        rig.UntilMs.ShouldBe(15);                     // 30 - 15 elapsed
        rig.Advance(14.9);
        rig.Scheduler.TryTakeDue(out _).ShouldBeFalse("just before the window ends");
        rig.Advance(0.1);

        var trailing = rig.Take();
        trailing.Reason.ShouldBe(EvaluationReason.Trailing);
        trailing.Signals.ShouldBe(SignalKinds.Navigation | SignalKinds.Foreground);   // the union of what was coalesced
        rig.Done(trailing);
        rig.Scheduler.TryTakeDue(out _).ShouldBeFalse("all four signals were served by one evaluation");
    }

    [Fact]
    public void REQ031_A_signal_after_the_window_is_a_new_leading_edge_not_a_trailing_one()
    {
        var rig = new SchedulerRig();
        rig.Scheduler.Signal(SignalKinds.Navigation);
        rig.Done(rig.Take());
        rig.Advance(30);

        rig.Scheduler.Signal(SignalKinds.Navigation);

        rig.UntilMs.ShouldBe(0);
        rig.Take().Reason.ShouldBe(EvaluationReason.Signal);
    }

    [Fact]
    public void REQ031_A_continuous_stream_costs_at_most_one_evaluation_per_window_and_the_last_state_is_read()
    {
        var rig = new SchedulerRig();
        double lastSignalAt = 0;
        for (int i = 0; i < 1000; i++)               // a signal every millisecond for one second
        {
            rig.Scheduler.Signal(SignalKinds.Navigation);
            lastSignalAt = rig.Elapsed.TotalMilliseconds;
            while (rig.Scheduler.TryTakeDue(out var r))
            {
                rig.Taken.Add((r, rig.Elapsed));
                rig.Done(r);
            }

            rig.Advance(1);
        }

        rig.Taken.Count.ShouldBeLessThanOrEqualTo(1000 / 30 + 1);
        rig.Taken.Count.ShouldBeGreaterThan(25);
        // after the stream stops, one more evaluation starts no later than one window after the final signal
        rig.Run(40, _ => Fg);
        rig.Taken[^1].At.TotalMilliseconds.ShouldBeGreaterThanOrEqualTo(lastSignalAt);
        (rig.Taken[^1].At.TotalMilliseconds - lastSignalAt).ShouldBeLessThanOrEqualTo(30 + 1);
        for (int i = 1; i < rig.Taken.Count; i++)
        {
            (rig.Taken[i].At - rig.Taken[i - 1].At).TotalMilliseconds.ShouldBeGreaterThanOrEqualTo(30 - 0.001);
        }
    }

    [Fact]
    public void REQ031_Sequences_strictly_increase()
    {
        var rig = new SchedulerRig();
        for (int i = 0; i < 5; i++)
        {
            rig.Scheduler.Signal(SignalKinds.Navigation);
            rig.TakeAndComplete();
            rig.Advance(31);
        }

        rig.Taken.Select(t => t.Request.Sequence).ShouldBe(rig.Taken.Select(t => t.Request.Sequence).OrderBy(x => x).Distinct());
    }

    [Fact]
    public void REQ031_A_signal_during_a_slow_evaluation_is_kept_and_served_after_it()
    {
        var rig = new SchedulerRig();
        rig.Scheduler.Signal(SignalKinds.Navigation);
        var first = rig.Take();

        rig.Advance(50);                                      // the evaluation is slow: past the 30 ms window
        rig.Scheduler.Signal(SignalKinds.Navigation);
        rig.Scheduler.TryTakeDue(out _).ShouldBeFalse("one evaluation at a time");
        rig.UntilMs.ShouldBeNull("nothing to wait for while one is in flight");

        rig.Done(first);

        rig.UntilMs.ShouldBe(0);
        rig.Take().Reason.ShouldBe(EvaluationReason.Signal);
    }

    [Fact]
    public void REQ031_Complete_ignores_a_sequence_that_is_not_in_flight()
    {
        var rig = new SchedulerRig();
        rig.Scheduler.Signal(SignalKinds.Navigation);
        var r = rig.Take();

        rig.Scheduler.Complete(r.Sequence + 99, new EvaluationOutcome(true, true, EvaluationResultKind.Resolved));

        rig.Scheduler.EvaluationInFlight.ShouldBeTrue();
        rig.Done(r);
        rig.Scheduler.EvaluationInFlight.ShouldBeFalse();
    }

    // ---- confirm pass -------------------------------------------------------------------------------

    [Fact]
    public void REQ031_A_changed_state_schedules_one_confirm_75ms_later()
    {
        var rig = new SchedulerRig();
        rig.Scheduler.Signal(SignalKinds.Navigation);
        rig.TakeAndComplete(changed: true);

        rig.UntilMs.ShouldBe(75);
        rig.Advance(74.9);
        rig.Scheduler.TryTakeDue(out _).ShouldBeFalse();
        rig.Advance(0.1);
        var c = rig.Take();
        c.Reason.ShouldBe(EvaluationReason.Confirm);
    }

    [Fact]
    public void REQ031_An_unchanged_state_schedules_no_confirm()
    {
        var rig = new SchedulerRig();
        rig.Scheduler.Signal(SignalKinds.Navigation);
        rig.TakeAndComplete(changed: false);

        rig.Advance(74);
        rig.Scheduler.TryTakeDue(out _).ShouldBeFalse();
    }

    [Fact]
    public void REQ031_A_confirm_that_changes_the_state_does_not_chain_another_confirm()
    {
        var rig = new SchedulerRig();
        rig.Scheduler.Signal(SignalKinds.Navigation);
        rig.TakeAndComplete(changed: true);
        rig.Advance(75);

        var confirm = rig.Take();
        confirm.Reason.ShouldBe(EvaluationReason.Confirm);
        rig.Done(confirm, changed: true);

        rig.Advance(75);
        rig.Scheduler.TryTakeDue(out var next).ShouldBeFalse(next.Reason.ToString());   // nothing until the poll
    }

    [Fact]
    public void REQ031_A_signal_before_the_confirm_replaces_it_with_the_read_that_signal_causes()
    {
        var rig = new SchedulerRig();
        rig.Scheduler.Signal(SignalKinds.Navigation);
        rig.TakeAndComplete(changed: true);
        rig.Advance(40);

        rig.Scheduler.Signal(SignalKinds.Navigation);
        var r = rig.TakeAndComplete(changed: false);

        r.Reason.ShouldBe(EvaluationReason.Signal);
        rig.Advance(200);
        rig.Scheduler.TryTakeDue(out var after).ShouldBeFalse(after.Reason.ToString());   // the old confirm is gone
    }

    // ---- retry ladder -------------------------------------------------------------------------------

    [Fact]
    public void REQ031_Unresolved_climbs_the_ladder_at_50_150_400_1000_ms_from_the_first_failure_then_stops()
    {
        var rig = new SchedulerRig(new SchedulerOptions { PollFast = TimeSpan.FromHours(1), PollSlow = TimeSpan.FromHours(1), ReconcileEvery = TimeSpan.FromHours(1) });
        rig.Scheduler.Signal(SignalKinds.Foreground);
        var first = rig.Take();
        rig.Done(first, kind: EvaluationResultKind.Unresolved);      // t = 0 (poll and reconcile pushed far away to isolate the ladder)

        double[] rungs = [50, 150, 400, 1000];
        double elapsed = 0;
        CompletionAdvice last = default;
        for (int i = 0; i < rungs.Length; i++)
        {
            double wait = rungs[i] - elapsed;
            rig.UntilMs.ShouldBe(wait, $"rung {i}");
            rig.Advance(wait - 0.1);
            rig.Scheduler.TryTakeDue(out _).ShouldBeFalse($"rung {i} must not fire early");
            rig.Advance(0.1);
            elapsed = rungs[i];
            var retry = rig.Take();
            retry.Reason.ShouldBe(EvaluationReason.Retry);
            retry.RetryAttempt.ShouldBe(i);
            last = rig.Done(retry, kind: EvaluationResultKind.Unresolved);
            if (i < rungs.Length - 1)
            {
                last.LadderExhausted.ShouldBeFalse();
            }
        }

        last.LadderExhausted.ShouldBeTrue("the 4th rung failed too: the host may mark the frame unresolved");
        rig.Scheduler.TimeUntilNextDue()!.Value.TotalMilliseconds.ShouldBeGreaterThan(0);   // only the poll remains
        rig.Taken.Count(t => t.Request.Reason == EvaluationReason.Retry).ShouldBe(4);
    }

    [Fact]
    public void REQ031_A_successful_retry_ends_the_ladder()
    {
        var rig = new SchedulerRig();
        rig.Scheduler.Signal(SignalKinds.Foreground);
        rig.TakeAndComplete(kind: EvaluationResultKind.Transient);
        rig.Advance(50);

        rig.TakeAndComplete(changed: true, kind: EvaluationResultKind.Resolved).Reason.ShouldBe(EvaluationReason.Retry);

        rig.Run(120, r => r.Reason == EvaluationReason.Confirm ? Fg : Fg);
        rig.Taken.Count(t => t.Request.Reason == EvaluationReason.Retry).ShouldBe(1);
    }

    [Fact]
    public void REQ031_A_late_failure_still_gets_every_rung_overdue_ones_run_immediately()
    {
        var rig = new SchedulerRig();
        rig.Scheduler.Signal(SignalKinds.Foreground);
        var first = rig.Take();
        rig.Advance(200);                                               // the failing evaluation took 200 ms
        rig.Done(first, kind: EvaluationResultKind.Unresolved);

        rig.UntilMs.ShouldBe(0);                                        // rungs +50 and +150 are already overdue
        rig.Take().RetryAttempt.ShouldBe(0);
    }

    [Fact]
    public void REQ031_A_failing_poll_does_not_start_a_ladder_so_persistent_failure_costs_one_read_per_poll()
    {
        var rig = new SchedulerRig();
        rig.Scheduler.Signal(SignalKinds.Foreground);
        rig.TakeAndComplete(kind: EvaluationResultKind.Resolved);
        rig.Advance(500);
        var poll = rig.Take();
        poll.Reason.ShouldBe(EvaluationReason.Poll);

        var advice = rig.Done(poll, kind: EvaluationResultKind.Unresolved);

        advice.LadderExhausted.ShouldBeFalse();
        rig.UntilMs.ShouldBe(500);                                      // just the next poll
    }

    [Fact]
    public void REQ031_The_ladder_rungs_are_not_taken_over_by_a_poll()
    {
        var rig = new SchedulerRig();
        rig.Scheduler.Signal(SignalKinds.Foreground);
        rig.TakeAndComplete(kind: EvaluationResultKind.Unresolved);     // ladder starts at t=0; poll due at 500

        rig.Run(1100, r => new EvaluationOutcome(true, false, EvaluationResultKind.Unresolved));

        rig.Taken.Count(t => t.Request.Reason == EvaluationReason.Retry).ShouldBe(4);   // incl. the +1000 rung after the poll at +500
    }

    // ---- poll ---------------------------------------------------------------------------------------

    [Fact]
    public void NFR06_Poll_runs_at_500ms_for_10s_after_the_last_signal_then_backs_off_to_2s()
    {
        var rig = new SchedulerRig();
        rig.Scheduler.Signal(SignalKinds.Foreground);
        rig.TakeAndComplete();
        var polls = new List<double>();
        rig.Run(20_000, r => { if (r.Reason == EvaluationReason.Poll) { polls.Add(rig.Elapsed.TotalMilliseconds); } return Fg; });

        // gaps: 500 ms while the last signal is < 10 s old, 2000 ms afterwards
        var gaps = polls.Zip(polls.Skip(1), (a, b) => b - a).ToList();
        gaps.Take(18).ShouldAllBe(g => Math.Abs(g - 500) < 2);
        gaps.Skip(21).ShouldAllBe(g => Math.Abs(g - 2000) < 2);
        gaps.Max().ShouldBeLessThanOrEqualTo(2002);          // never slower than 2 s: bounded staleness (04 D.7a)
    }

    [Fact]
    public void NFR06_A_signal_returns_the_poll_to_fast()
    {
        var rig = new SchedulerRig();
        rig.Scheduler.Signal(SignalKinds.Foreground);
        rig.TakeAndComplete();
        rig.Run(12_000, _ => Fg);                            // now in slow mode

        rig.Scheduler.Signal(SignalKinds.Navigation);
        rig.TakeAndComplete();

        rig.UntilMs.ShouldBe(500);
    }

    [Fact]
    public void NFR06_There_is_no_timer_at_all_while_Explorer_is_not_foreground()
    {
        var rig = new SchedulerRig();
        rig.Scheduler.Signal(SignalKinds.Foreground);
        rig.TakeAndComplete(foreground: false);

        rig.UntilMs.ShouldBeNull();
        rig.Advance(120_000);
        rig.Scheduler.TryTakeDue(out _).ShouldBeFalse();
        rig.Scheduler.ExplorerForeground.ShouldBeFalse();
    }

    [Fact]
    public void NFR06_Losing_the_foreground_clears_confirm_retry_poll_and_reconcile_but_a_pending_signal_is_still_served()
    {
        var rig = new SchedulerRig();
        rig.Scheduler.Signal(SignalKinds.Foreground);
        rig.TakeAndComplete(changed: true, kind: EvaluationResultKind.Resolved);   // confirm armed
        rig.Advance(30);
        rig.Scheduler.Signal(SignalKinds.Foreground);
        var r = rig.Take();
        rig.Scheduler.Signal(SignalKinds.Foreground);                 // arrives while in flight
        rig.Done(r, foreground: false);

        rig.Advance(30);                                               // the trailing edge of the window
        var pending = rig.Take();                                      // the signal is honoured
        rig.Done(pending, foreground: false);
        rig.UntilMs.ShouldBeNull();
    }

    // ---- reconcile ----------------------------------------------------------------------------------

    [Fact]
    public void REQ034_Reconcile_is_due_30s_after_Explorer_became_foreground_and_asks_for_a_rebuild()
    {
        var rig = new SchedulerRig();
        rig.Scheduler.Signal(SignalKinds.Foreground);
        var first = rig.TakeAndComplete();
        first.Rebuild.ShouldBeFalse();

        var rebuilds = new List<double>();
        rig.Run(95_000, r => { if (r.Rebuild) { rebuilds.Add(rig.Elapsed.TotalMilliseconds); } return Fg; });

        rebuilds.Count.ShouldBe(3);
        rebuilds[0].ShouldBe(30_000, tolerance: 2);
        (rebuilds[1] - rebuilds[0]).ShouldBe(30_000, tolerance: 2);
    }

    [Fact]
    public void REQ034_A_due_reconcile_rides_on_whatever_evaluation_runs_next_and_is_then_rescheduled()
    {
        var rig = new SchedulerRig();
        rig.Scheduler.Signal(SignalKinds.Foreground);
        rig.TakeAndComplete();
        rig.Advance(30_000);
        rig.Scheduler.Signal(SignalKinds.Navigation);

        var r = rig.Take();

        r.Rebuild.ShouldBeTrue();
        rig.Done(r);
        rig.Advance(100);
        rig.Scheduler.Signal(SignalKinds.Navigation);
        rig.Take().Rebuild.ShouldBeFalse();
    }

    [Fact]
    public void NFR06_No_reconcile_in_the_background()
    {
        var rig = new SchedulerRig();
        rig.Scheduler.Signal(SignalKinds.Foreground);
        rig.TakeAndComplete(foreground: true);
        rig.Scheduler.Signal(SignalKinds.Foreground);
        rig.Advance(31);
        rig.TakeAndComplete(foreground: false);

        rig.Advance(120_000);
        rig.Scheduler.TryTakeDue(out _).ShouldBeFalse();
    }

    // ---- window respected by timed evaluations ---------------------------------------------------

    [Fact]
    public void REQ031_A_timed_evaluation_never_starts_inside_a_throttle_window()
    {
        // a wide window (100 ms) so that the first retry rung (+50 ms) falls inside it
        var rig = new SchedulerRig(new SchedulerOptions { ThrottleWindow = TimeSpan.FromMilliseconds(100) });
        rig.Scheduler.Signal(SignalKinds.Foreground);
        rig.TakeAndComplete(kind: EvaluationResultKind.Unresolved);

        rig.UntilMs.ShouldBe(100);                            // the rung is deferred to the end of the window
        rig.Advance(99.9);
        rig.Scheduler.TryTakeDue(out _).ShouldBeFalse();
        rig.Advance(0.1);
        rig.Take().Reason.ShouldBe(EvaluationReason.Retry);
    }
}
