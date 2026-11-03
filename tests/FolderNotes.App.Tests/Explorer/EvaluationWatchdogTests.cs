using FolderNotes.App.Explorer;
using Microsoft.Extensions.Time.Testing;

namespace FolderNotes.App.Tests.Explorer;

/// <summary>04 B.6 timing: cancel at 250 ms into an evaluation, abandon 1 s after the cancel; boundaries inclusive.</summary>
[Trait("Kind", "Deterministic")]
public class EvaluationWatchdogTests
{
    private readonly FakeTimeProvider _clock = new();
    private readonly EvaluationWatchdog _dog;

    public EvaluationWatchdogTests() => _dog = new EvaluationWatchdog(_clock);

    private void Ms(double ms) => _clock.Advance(TimeSpan.FromMilliseconds(ms));

    [Fact]
    public void NFR01_Nothing_happens_without_an_evaluation()
    {
        Ms(10_000);
        _dog.Check().ShouldBe(WatchdogAction.None);
        _dog.TimeUntilNextCheck().ShouldBeNull();
    }

    [Fact]
    public void NFR01_Cancel_fires_exactly_at_250ms_not_before()
    {
        _dog.Begin();
        Ms(249.999);
        _dog.Check().ShouldBe(WatchdogAction.None);
        Ms(0.001);
        _dog.Check().ShouldBe(WatchdogAction.Cancel);
    }

    [Fact]
    public void NFR01_Cancel_is_issued_once_per_evaluation()
    {
        _dog.Begin();
        Ms(250);
        _dog.Check().ShouldBe(WatchdogAction.Cancel);
        Ms(100);
        _dog.Check().ShouldBe(WatchdogAction.None);
        Ms(100);
        _dog.Check().ShouldBe(WatchdogAction.None);
    }

    [Fact]
    public void NFR01_Abandon_fires_exactly_1s_after_the_cancel_not_before()
    {
        _dog.Begin();
        Ms(250);
        _dog.Check().ShouldBe(WatchdogAction.Cancel);
        Ms(999.999);
        _dog.Check().ShouldBe(WatchdogAction.None);
        Ms(0.001);
        _dog.Check().ShouldBe(WatchdogAction.Abandon);
        _dog.Abandoned.ShouldBeTrue();
    }

    [Fact]
    public void NFR01_The_abandon_clock_runs_from_the_cancel_even_if_the_watchdog_checked_late()
    {
        _dog.Begin();
        Ms(400);                                              // the watchdog thread was late: cancel issued now
        _dog.Check().ShouldBe(WatchdogAction.Cancel);
        Ms(999);
        _dog.Check().ShouldBe(WatchdogAction.None);
        Ms(1);
        _dog.Check().ShouldBe(WatchdogAction.Abandon);
    }

    [Fact]
    public void NFR01_Abandon_is_returned_once_and_the_dog_goes_quiet()
    {
        _dog.Begin();
        Ms(250);
        _dog.Check();
        Ms(1000);
        _dog.Check().ShouldBe(WatchdogAction.Abandon);
        Ms(10_000);
        _dog.Check().ShouldBe(WatchdogAction.None);
        _dog.TimeUntilNextCheck().ShouldBeNull();
    }

    [Fact]
    public void NFR01_A_cancelled_call_that_returns_resets_the_dog_for_the_next_evaluation()
    {
        _dog.Begin();
        Ms(250);
        _dog.Check().ShouldBe(WatchdogAction.Cancel);
        Ms(5);
        _dog.End();                                           // the call came back RPC_E_CALL_CANCELED

        Ms(5000);
        _dog.Check().ShouldBe(WatchdogAction.None);
        _dog.Begin();
        Ms(249);
        _dog.Check().ShouldBe(WatchdogAction.None);
        Ms(1);
        _dog.Check().ShouldBe(WatchdogAction.Cancel);         // a full 250 ms again, and a new cancel
    }

    [Fact]
    public void NFR01_A_fast_evaluation_never_triggers_anything()
    {
        for (int i = 0; i < 100; i++)
        {
            _dog.Begin();
            Ms(10);
            _dog.End();
            Ms(5000);
            _dog.Check().ShouldBe(WatchdogAction.None);
        }
    }

    [Fact]
    public void NFR01_An_abandoned_worker_cannot_re_arm_the_dog()
    {
        _dog.Begin();
        Ms(250);
        _dog.Check();
        Ms(1000);
        _dog.Check().ShouldBe(WatchdogAction.Abandon);

        _dog.Begin();                                         // the zombie thread wakes up and starts another call
        Ms(10_000);
        _dog.Check().ShouldBe(WatchdogAction.None);
        _dog.TimeUntilNextCheck().ShouldBeNull();
    }

    [Fact]
    public void NFR01_TimeUntilNextCheck_tracks_the_next_deadline()
    {
        _dog.Begin();
        _dog.TimeUntilNextCheck().ShouldBe(TimeSpan.FromMilliseconds(250));
        Ms(100);
        _dog.TimeUntilNextCheck().ShouldBe(TimeSpan.FromMilliseconds(150));
        Ms(150);
        _dog.TimeUntilNextCheck().ShouldBe(TimeSpan.Zero);
        _dog.Check().ShouldBe(WatchdogAction.Cancel);
        _dog.TimeUntilNextCheck().ShouldBe(TimeSpan.FromSeconds(1));
        Ms(400);
        _dog.TimeUntilNextCheck().ShouldBe(TimeSpan.FromMilliseconds(600));
    }

    [Fact]
    public void NFR01_Custom_limits_are_honoured()
    {
        var dog = new EvaluationWatchdog(_clock, new WatchdogOptions { CancelAfter = TimeSpan.FromMilliseconds(40), AbandonAfterCancel = TimeSpan.FromMilliseconds(60) });
        dog.Begin();
        Ms(40);
        dog.Check().ShouldBe(WatchdogAction.Cancel);
        Ms(60);
        dog.Check().ShouldBe(WatchdogAction.Abandon);
    }
}
