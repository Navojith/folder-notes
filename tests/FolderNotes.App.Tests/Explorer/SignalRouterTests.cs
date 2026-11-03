using FolderNotes.App.Explorer;

namespace FolderNotes.App.Tests.Explorer;

/// <summary>04 B.7 / J (H3, H4, H5): the router between the hook callbacks and the current worker.</summary>
[Trait("Kind", "Deterministic")]
public class SignalRouterTests
{
    private sealed class Target : IWakeTarget
    {
        public int Posts;
        public bool Result = true;
        public bool Throw;

        public bool TryPost()
        {
            if (Throw)
            {
                throw new InvalidOperationException("boom");
            }

            Interlocked.Increment(ref Posts);
            return Result;
        }
    }

    [Fact]
    public void NFR01_Bits_raised_before_a_worker_exists_wait_and_wake_it_on_Attach()
    {
        var r = new SignalRouter();
        r.Raise(TrackerSignal.NameChange);
        r.Raise(TrackerSignal.ShowHide);
        r.Pending.ShouldBe(TrackerSignal.NameChange | TrackerSignal.ShowHide);

        var t = new Target();
        r.Attach(t);

        t.Posts.ShouldBe(1);
        r.Consume(t).ShouldBe(TrackerSignal.NameChange | TrackerSignal.ShowHide);
        r.Pending.ShouldBe(TrackerSignal.None);
    }

    [Fact]
    public void NFR06_A_storm_of_events_leaves_at_most_one_wake_up_outstanding()
    {
        var r = new SignalRouter();
        var t = new Target();
        r.Attach(t);

        for (int i = 0; i < 10_000; i++)
        {
            r.Raise(TrackerSignal.NameChange);
        }

        t.Posts.ShouldBe(1);
        r.SignalsRaised.ShouldBe(10_000);
        r.Consume(t).ShouldBe(TrackerSignal.NameChange);

        r.Raise(TrackerSignal.NameChange);                    // after the worker read it, the next event posts again
        t.Posts.ShouldBe(2);
    }

    [Fact]
    public void NFR01_Events_during_an_evaluation_post_again_because_the_worker_cleared_first()
    {
        var r = new SignalRouter();
        var t = new Target();
        r.Attach(t);
        r.Raise(TrackerSignal.Foreground);
        r.Consume(t).ShouldBe(TrackerSignal.Foreground);       // the worker starts evaluating here

        r.Raise(TrackerSignal.ShowHide);                       // an event arrives mid-evaluation

        t.Posts.ShouldBe(2);
        r.Consume(t).ShouldBe(TrackerSignal.ShowHide);
    }

    [Fact]
    public void NFR01_A_detached_worker_gets_no_wake_ups_and_takes_no_bits()
    {
        var r = new SignalRouter();
        var old = new Target();
        r.Attach(old);
        r.Raise(TrackerSignal.NameChange);
        old.Posts.ShouldBe(1);

        r.Detach(old).ShouldBeTrue();
        r.Raise(TrackerSignal.ShowHide);

        old.Posts.ShouldBe(1);
        r.Consume(old).ShouldBe(TrackerSignal.None);          // a zombie waking up late takes nothing
        r.Pending.ShouldBe(TrackerSignal.NameChange | TrackerSignal.ShowHide);
    }

    [Fact]
    public void NFR01_Replacing_a_worker_hands_the_waiting_bits_to_the_replacement_only()
    {
        var r = new SignalRouter();
        var old = new Target();
        var fresh = new Target();
        r.Attach(old);
        r.Raise(TrackerSignal.NameChange);                     // old has an unread wake-up (it is wedged)

        r.Detach(old);
        r.Attach(fresh);

        fresh.Posts.ShouldBe(1);                               // the replacement is woken for what the zombie never read
        r.Consume(old).ShouldBe(TrackerSignal.None);
        r.Consume(fresh).ShouldBe(TrackerSignal.NameChange);
    }

    [Fact]
    public void NFR01_A_stale_Detach_does_not_remove_the_current_worker()
    {
        var r = new SignalRouter();
        var old = new Target();
        var fresh = new Target();
        r.Attach(old);
        r.Attach(fresh);

        r.Detach(old).ShouldBeFalse();

        r.IsAttached.ShouldBeTrue();
        r.Raise(TrackerSignal.Foreground);
        fresh.Posts.ShouldBe(1);
    }

    [Fact]
    public void NFR01_A_failed_post_is_retried_by_the_next_event_and_never_throws_out_of_a_callback()
    {
        var r = new SignalRouter();
        var t = new Target { Result = false };
        r.Attach(t);

        r.Raise(TrackerSignal.NameChange);
        r.Raise(TrackerSignal.NameChange);
        t.Posts.ShouldBe(2);                                   // the failure did not leave a phantom "outstanding" flag

        t.Throw = true;
        Should.NotThrow(() => r.Raise(TrackerSignal.ShowHide));
        t.Throw = false;
        t.Result = true;
        r.Raise(TrackerSignal.Minimize);
        r.Consume(t).ShouldBe(TrackerSignal.NameChange | TrackerSignal.ShowHide | TrackerSignal.Minimize);
    }

    // ---- H4, deterministic: force each interleaving with the router's test seams (no threads, no repetition) ----

    /// <summary>
    /// The invariant that "no signal is lost" means: after any consume, a bit that was raised is either in the returned
    /// bits, or still pending with a wake-up posted after the consume started, so someone will read it.
    /// </summary>
    private static void AssertNotStranded(SignalRouter r, Target t, int postsBeforeConsume, TrackerSignal taken, TrackerSignal raised)
    {
        var missing = raised & ~taken;
        if (missing != TrackerSignal.None)
        {
            r.Pending.HasFlag(missing).ShouldBeTrue("the bit vanished");
            t.Posts.ShouldBeGreaterThan(postsBeforeConsume, "a bit is pending but no wake-up was posted for it: stranded");
        }
    }

    [Fact]
    public void NFR01_H4_Interleaving_A_a_raise_lands_between_the_consumers_clear_and_take_is_not_lost()
    {
        var r = new SignalRouter();
        var t = new Target();
        r.Attach(t);
        r.Raise(TrackerSignal.Foreground);                    // outstanding = 1, bit pending: the worker is about to read
        int posts = t.Posts;
        r.GapInConsume = () => { r.GapInConsume = null; r.Raise(TrackerSignal.ShowHide); };   // lands in the gap, once

        var taken = r.Consume(t);

        AssertNotStranded(r, t, posts, taken, TrackerSignal.Foreground | TrackerSignal.ShowHide);
        // correct order: the raise saw outstanding = 0, so it posted its own wake-up AND its bit was taken
        taken.ShouldBe(TrackerSignal.Foreground | TrackerSignal.ShowHide);
        t.Posts.ShouldBe(posts + 1);                          // the spurious wake-up is the price of never losing one
        r.Consume(t).ShouldBe(TrackerSignal.None);            // ...and it finds nothing
    }

    [Fact]
    public void NFR01_H4_Interleaving_B_a_raise_lands_before_the_consumers_clear_is_taken_by_it()
    {
        var r = new SignalRouter();
        var t = new Target();
        r.Attach(t);
        r.Raise(TrackerSignal.Foreground);
        int posts = t.Posts;
        r.Raise(TrackerSignal.ShowHide);                      // before the worker's clear: no new post (one is outstanding)
        t.Posts.ShouldBe(posts);

        var taken = r.Consume(t);

        taken.ShouldBe(TrackerSignal.Foreground | TrackerSignal.ShowHide);
        AssertNotStranded(r, t, posts, taken, TrackerSignal.Foreground | TrackerSignal.ShowHide);
        r.Pending.ShouldBe(TrackerSignal.None);
    }

    [Fact]
    public void NFR01_H4_Interleaving_C_a_whole_consume_between_the_raisers_record_and_post_is_not_lost()
    {
        var r = new SignalRouter();
        var t = new Target();
        r.Attach(t);
        TrackerSignal taken = TrackerSignal.None;
        r.GapInRaise = () => { r.GapInRaise = null; taken = r.Consume(t); };   // the worker runs between the raise's two steps

        r.Raise(TrackerSignal.NameChange);

        taken.ShouldBe(TrackerSignal.NameChange);             // already recorded, so the worker saw it
        r.Pending.ShouldBe(TrackerSignal.None);
        t.Posts.ShouldBe(0);                                  // Kick sees nothing pending, so no spurious wake-up is posted
        r.Consume(t).ShouldBe(TrackerSignal.None);
    }

    [Fact]
    public void NFR01_H4_Interleaving_D_a_second_raise_between_record_and_post_posts_once()
    {
        var r = new SignalRouter();
        var t = new Target();
        r.Attach(t);
        r.GapInRaise = () => { r.GapInRaise = null; r.Raise(TrackerSignal.ShowHide); };

        r.Raise(TrackerSignal.NameChange);

        t.Posts.ShouldBe(1);                                  // two racing raisers, one outstanding wake-up
        r.Consume(t).ShouldBe(TrackerSignal.NameChange | TrackerSignal.ShowHide);
    }

    [Fact]
    public void NFR01_H4_The_seams_are_off_in_production_objects()
    {
        var r = new SignalRouter();
        r.GapInConsume.ShouldBeNull();
        r.GapInRaise.ShouldBeNull();
    }

    /// <summary>
    /// The H4 stress test (a smoke test only; the deterministic interleavings above are the guarantee): in each round two producers race a consumer that is still handling the previous wake-up, then the
    /// round must go quiet. A lost wake-up leaves a bit pending with nobody to read it, which the quiet check catches
    /// (the next round's raise would otherwise hide it, so every round ends with the check).
    /// </summary>
    [Fact]
    [Trait("Kind", "Timing")]
    public async Task NFR01_No_signal_is_ever_lost_under_concurrent_raises_and_consumes()
    {
        var r = new SignalRouter();
        using var wake = new SemaphoreSlim(0);
        var target = new WakingTarget(wake);
        r.Attach(target);
        int seen = 0;
        using var stop = new CancellationTokenSource();

        var consumer = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                if (wake.Wait(20, CancellationToken.None))
                {
                    seen |= (int)r.Consume(target);
                }
            }
        }, TestContext.Current.CancellationToken);

        TrackerSignal[] bits = [TrackerSignal.Foreground, TrackerSignal.NameChange, TrackerSignal.ShowHide, TrackerSignal.Minimize, TrackerSignal.CreateDestroy];
        int raisedUnion = 0;
        for (int round = 0; round < 4_000; round++)
        {
            int rr = round;
            var t1 = Task.Run(() => RaiseBurst(r, bits[rr % 5], ref raisedUnion), TestContext.Current.CancellationToken);
            var t2 = Task.Run(() => RaiseBurst(r, bits[(rr + 2) % 5], ref raisedUnion), TestContext.Current.CancellationToken);
            await Task.WhenAll(t1, t2);
            SpinWait.SpinUntil(() => r.Pending == TrackerSignal.None, TimeSpan.FromSeconds(2))
                .ShouldBeTrue($"round {round}: a raised signal stayed pending with no wake-up: lost wake-up");
        }

        stop.Cancel();
        await consumer.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        (seen & raisedUnion).ShouldBe(raisedUnion);
        r.WakeUpsPosted.ShouldBeLessThanOrEqualTo(r.SignalsRaised);
    }

    private static void RaiseBurst(SignalRouter r, TrackerSignal bit, ref int union)
    {
        Interlocked.Or(ref union, (int)bit);
        r.Raise(bit);
        Thread.SpinWait(50);
        r.Raise(bit);
    }

    private sealed class WakingTarget(SemaphoreSlim wake) : IWakeTarget
    {
        public bool TryPost()
        {
            wake.Release();
            return true;
        }
    }
}
