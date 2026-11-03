using FolderNotes.App.Explorer;
using Microsoft.Extensions.Time.Testing;

namespace FolderNotes.App.Tests.Explorer;

/// <summary>04 § M.3–M.5: suspects, evidence E1/E2/E3, the health hold and the per-server automatic trial budget. Fake clock, fake probe.</summary>
[Trait("Kind", "Deterministic")]
public class QuarantineRecordTests
{
    private readonly FakeTimeProvider _clock = new();
    private readonly FakeServerProbe _probe = new();
    private readonly QuarantineRecord _record;

    public QuarantineRecordTests() => _record = new QuarantineRecord(_clock);

    private void Seconds(double s) => _clock.Advance(TimeSpan.FromSeconds(s));

    // ---- suspects (M.3) ---------------------------------------------------------------------------------

    [Fact]
    public void NFR01_M3_An_active_record_with_a_pid_names_a_known_server_by_pid_and_creation_time()
    {
        var server = _probe.Run(42);

        var s = Suspect.From(TestEntries.ActiveSnapshot(CallSite.PathRead, 42), _probe);

        s.Kind.ShouldBe(SuspectKind.Known);
        s.Servers.ShouldBe([server]);
    }

    [Fact]
    public void NFR01_M3_An_active_record_without_a_pid_gives_every_Explorer_process_and_the_shell_as_candidates()
    {
        var a = _probe.Run(10);
        var b = _probe.Run(11);
        var shell = _probe.Run(500, ownsFrame: false);
        _probe.ShellPid = 500;

        var s = Suspect.From(TestEntries.ActiveSnapshot(CallSite.ItemProbe, 0), _probe);

        s.Kind.ShouldBe(SuspectKind.Candidates);
        s.Servers.ShouldBe([a, b, shell], ignoreOrder: true);
    }

    [Theory]
    [InlineData((int)JournalReadKind.Idle)]
    [InlineData((int)JournalReadKind.Indeterminate)]
    [InlineData((int)JournalReadKind.Stale)]
    public void NFR01_M2_An_idle_torn_or_stale_record_never_gives_a_known_suspect(int kindValue)
    {
        var kind = (JournalReadKind)kindValue;
        _probe.Run(42);
        var snap = new JournalSnapshot(kind, new CallRecord(1, 5, CallSite.PathRead, 42, 0, -1, 0));

        Suspect.From(snap, _probe).Kind.ShouldBe(SuspectKind.Unknown);
    }

    [Fact]
    public void NFR01_M3_More_than_eight_candidates_is_unknown()
    {
        for (uint p = 1; p <= 9; p++)
        {
            _probe.Run(p);
        }

        Suspect.From(TestEntries.ActiveSnapshot(CallSite.ItemProbe, 0), _probe).Kind.ShouldBe(SuspectKind.Unknown);
    }

    [Fact]
    public void NFR01_M5_Same_server_is_compared_conservatively()
    {
        var a = new ServerIdentity(1, 100);
        var b = new ServerIdentity(2, 200);
        var a2 = new ServerIdentity(1, 101);                    // pid 1 reused by another process

        Suspect.KnownServer(a).SameServerAs(Suspect.KnownServer(a)).ShouldBeTrue();
        Suspect.KnownServer(a).SameServerAs(Suspect.KnownServer(b)).ShouldBeFalse();
        Suspect.KnownServer(a).SameServerAs(Suspect.KnownServer(a2)).ShouldBeFalse();
        Suspect.Unknown.SameServerAs(Suspect.KnownServer(b)).ShouldBeTrue();
        Suspect.KnownServer(b).SameServerAs(Suspect.Unknown).ShouldBeTrue();
        new Suspect(SuspectKind.Candidates, [a, b]).SameServerAs(Suspect.KnownServer(b)).ShouldBeTrue();
    }

    // ---- evidence (M.4) ---------------------------------------------------------------------------------

    [Fact]
    public void NFR01_M4_E1_The_blocked_operation_completing_resolves_the_entry_whatever_the_suspect()
    {
        var z = new FakeZombie();
        var e = TestEntries.Known(z, _probe.Run(42), _clock);
        _record.Add(e);
        _record.Assess(_probe).AllResolved.ShouldBeFalse();

        z.Complete(e.Snapshot.Record.OperationId);

        _record.Assess(_probe).AllResolved.ShouldBeTrue();
        e.Resolution.ShouldBe(QuarantineResolution.OperationCompleted);
        _record.Releases.ByCompletion.ShouldBe(1);
    }

    [Fact]
    public void NFR01_M4_E1_The_zombie_thread_ending_resolves_even_an_unknown_entry()
    {
        var z = new FakeZombie();
        _record.Add(TestEntries.Unknown(z, _clock));
        Seconds(3600);
        _record.Assess(_probe).AllResolved.ShouldBeFalse();     // time alone is never evidence

        z.Exit();

        _record.Assess(_probe).AllResolved.ShouldBeTrue();
    }

    [Fact]
    public void NFR01_M4_E2_A_known_server_that_exits_resolves_the_entry()
    {
        var server = _probe.Run(42);
        _record.Add(TestEntries.Known(new FakeZombie(), server, _clock));

        _probe.Exit(42);

        _record.Assess(_probe).AllResolved.ShouldBeTrue();
        _record.Releases.ByServerEnd.ShouldBe(1);
    }

    [Fact]
    public void NFR01_M4_E2_A_reused_pid_is_a_replacement_not_the_same_server()
    {
        var server = _probe.Run(42);
        _record.Add(TestEntries.Known(new FakeZombie(), server, _clock));

        _probe.Exit(42);
        _probe.Run(42);                                         // a new process got the same pid

        _record.Assess(_probe).AllResolved.ShouldBeTrue();      // the suspect (old creation time) has ended
    }

    [Fact]
    public void NFR01_M4_E2_A_candidates_entry_resolves_only_when_every_candidate_ended_and_an_unknown_one_never()
    {
        var a = _probe.Run(10);
        var b = _probe.Run(11);
        _record.Add(new QuarantineEntry(new FakeZombie(), JournalSnapshot.Idle, new Suspect(SuspectKind.Candidates, [a, b]), null, _clock.GetTimestamp()));
        _record.Add(TestEntries.Unknown(new FakeZombie(), _clock));

        _probe.Exit(10);
        _record.Assess(_probe).AllResolved.ShouldBeFalse();
        _probe.Exit(11);
        _record.Assess(_probe);
        _record.Entries.Count.ShouldBe(1);                      // only the unknown one is left
        _record.Entries[0].Suspect.Kind.ShouldBe(SuspectKind.Unknown);
    }

    [Fact]
    public void NFR01_M1_A_successful_cancel_HRESULT_is_never_evidence()
    {
        var server = _probe.Run(42);
        _record.Add(TestEntries.Known(new FakeZombie(), server, _clock, cancelHr: 0));

        Seconds(60);

        _record.Assess(_probe).AllResolved.ShouldBeFalse();
        _record.Entries.Single().CancelHr.ShouldBe(0);
    }

    [Fact]
    public void NFR01_M4_A_candidate_set_narrows_to_the_one_candidate_that_shows_hang_evidence()
    {
        var a = _probe.Run(10);
        var b = _probe.Run(11);
        _record.Add(new QuarantineEntry(new FakeZombie(), JournalSnapshot.Idle, new Suspect(SuspectKind.Candidates, [a, b]), null, _clock.GetTimestamp()));

        _probe.Hang(11);
        _record.Assess(_probe);

        _record.Entries.Single().Suspect.Kind.ShouldBe(SuspectKind.Known);
        _record.Entries.Single().Suspect.Servers.ShouldBe([b]);
        _probe.Exit(11);
        _record.Assess(_probe).AllResolved.ShouldBeTrue();      // now E2 on the narrowed suspect is enough
    }

    // ---- health hold (E3) -------------------------------------------------------------------------------

    private QuarantineAssessment LookFor(double seconds)
    {
        for (double t = 0; t < seconds; t += 1)
        {
            _record.Assess(_probe);
            Seconds(1);
        }

        return _record.Assess(_probe);
    }

    [Fact]
    public void NFR01_M4_E3_The_hold_needs_10_s_clean_and_30_s_since_the_last_abandonment()
    {
        _record.Add(TestEntries.Known(new FakeZombie(), _probe.Run(42), _clock));

        LookFor(12).HoldComplete.ShouldBeFalse();               // 10 s clean, but only 12 s since the abandonment
        LookFor(18).HoldComplete.ShouldBeTrue();                // 30 s since the abandonment
    }

    [Fact]
    public void NFR01_M4_E3_Hang_evidence_restarts_the_hold_from_zero()
    {
        _record.Add(TestEntries.Known(new FakeZombie(), _probe.Run(42), _clock));
        LookFor(35);

        _probe.Hang(42);
        _record.Assess(_probe).HangEvidence.ShouldBeTrue();
        _probe.Hang(42, false);

        LookFor(8).HoldComplete.ShouldBeFalse();
        LookFor(3).HoldComplete.ShouldBeTrue();
    }

    [Fact]
    public void NFR01_M4_E3_The_hold_never_resolves_an_entry()
    {
        _record.Add(TestEntries.Known(new FakeZombie(), _probe.Run(42), _clock));

        LookFor(120).HoldComplete.ShouldBeTrue();

        _record.HasUnresolved.ShouldBeTrue();
    }

    [Fact]
    public void NFR01_M4_E3_An_unknown_suspect_shows_hang_evidence_when_any_Explorer_frame_is_hung()
    {
        _probe.Run(7);
        _record.Add(TestEntries.Unknown(new FakeZombie(), _clock));

        _probe.Hang(7);

        _record.Assess(_probe).HangEvidence.ShouldBeTrue();
        _record.HangEvidenceNow(_probe).ShouldBeTrue();
    }

    [Fact]
    public void NFR01_M4_A_new_abandonment_restarts_the_hold_and_the_minimum()
    {
        var server = _probe.Run(42);
        _record.Add(TestEntries.Known(new FakeZombie(), server, _clock));
        LookFor(40).HoldComplete.ShouldBeTrue();

        _record.Add(TestEntries.Known(new FakeZombie(), server, _clock));

        LookFor(20).HoldComplete.ShouldBeFalse();               // 30 s from the newest abandonment
        LookFor(10).HoldComplete.ShouldBeTrue();
    }

    // ---- per-server trial budget (M.5) ------------------------------------------------------------------

    [Fact]
    public void NFR01_M5_One_automatic_trial_per_server_a_different_server_earns_its_own()
    {
        var a = _probe.Run(42);
        var b = _probe.Run(43);
        _record.Add(TestEntries.Known(new FakeZombie(), a, _clock));
        _record.Assess(_probe).AutomaticTrialAllowed.ShouldBeTrue();

        _record.MarkTried();
        _record.Assess(_probe).AutomaticTrialAllowed.ShouldBeFalse();

        _record.Add(TestEntries.Known(new FakeZombie(), a, _clock));   // the same server wedged the trial
        _record.Assess(_probe).AutomaticTrialAllowed.ShouldBeFalse();

        _record.Add(TestEntries.Known(new FakeZombie(), b, _clock));   // a different server is new evidence
        _record.Assess(_probe).AutomaticTrialAllowed.ShouldBeTrue();
    }

    [Fact]
    public void NFR01_M5_An_unknown_suspect_counts_as_the_same_server_as_anything_tried()
    {
        _record.Add(TestEntries.Unknown(new FakeZombie(), _clock));
        _record.MarkTried();

        _record.Add(TestEntries.Known(new FakeZombie(), _probe.Run(42), _clock));

        _record.Assess(_probe).AutomaticTrialAllowed.ShouldBeFalse();
    }

    [Fact]
    public void NFR01_M5_When_the_record_empties_the_episode_ends_and_the_budget_resets()
    {
        var z = new FakeZombie();
        var server = _probe.Run(42);
        _record.Add(TestEntries.Known(z, server, _clock));
        _record.MarkTried();
        z.Exit();
        _record.Assess(_probe).AllResolved.ShouldBeTrue();

        _record.Add(TestEntries.Known(new FakeZombie(), server, _clock));   // a later, separate episode

        _record.Assess(_probe).AutomaticTrialAllowed.ShouldBeTrue();
        _record.Episodes.ShouldBe(2);
    }
}

/// <summary>04 § M.6: every row of the quarantine transition table on the pure machine (fake clock, fake probe).</summary>
[Trait("Kind", "Deterministic")]
public class QuarantineTransitionTests
{
    private readonly FakeTimeProvider _clock = new();
    private readonly FakeServerProbe _probe = new();
    private readonly List<ActiveFolderState> _delivered = [];
    private readonly StatePublisher _pub;
    private readonly TrackerProcessGuard _guard;
    private readonly ServerIdentity _server;
    private long _evidence;

    public QuarantineTransitionTests()
    {
        _pub = new StatePublisher(_delivered.Add);
        _guard = new TrackerProcessGuard(quarantine: new QuarantineRecord(_clock));
        _server = _probe.Run(42);
    }

    private QuarantineRecord Record => _guard.Quarantine;

    private TrackerStateMachine Machine(TrackerLimits? limits = null) => new(_clock, _pub, _guard, limits);

    private TrackerStateMachine Running(TrackerLimits? limits = null)
    {
        var m = Machine(limits);
        m.Start().ShouldBe(StartResult.Started);
        return m;
    }

    private QuarantineEntry Known(FakeZombie? z = null, ServerIdentity? server = null) => TestEntries.Known(z ?? new FakeZombie(), server ?? _server, _clock);

    private QuarantineAction Tick(TrackerStateMachine m) => m.OnQuarantineTick(Record.Assess(_probe), _evidence);

    /// <summary>Ticks once a second (the check interval) for <paramref name="seconds"/>; returns the first action other than None.</summary>
    private QuarantineAction TickFor(TrackerStateMachine m, int seconds)
    {
        for (int i = 0; i < seconds; i++)
        {
            var a = Tick(m);
            if (a != QuarantineAction.None)
            {
                return a;
            }

            _clock.Advance(TimeSpan.FromSeconds(1));
        }

        return QuarantineAction.None;
    }

    private TrackerStateMachine Quarantined(FakeZombie? z = null)
    {
        var m = Running();
        m.OnWedge(Known(z)).Outcome.ShouldBe(WedgeOutcome.Quarantined);
        return m;
    }

    // ---- entering ---------------------------------------------------------------------------------------

    [Fact]
    public void NFR01_Q1_At_the_cap_a_wedge_is_ThreadCap_and_the_entry_is_still_recorded()
    {
        var m = Running(new TrackerLimits { MaxAbandonedThreads = 1 });

        m.OnWedge(Known()).Reason.ShouldBe(DisabledReason.ThreadCap);

        m.Availability.ShouldBe(TrackerAvailability.PermanentlyOff);
        Record.HasUnresolved.ShouldBeTrue();
    }

    [Fact]
    public void NFR01_Q2_A_breaker_trip_records_the_entry_for_a_later_Retry()
    {
        var m = Running(new TrackerLimits { BreakerWedges = 1 });

        m.OnWedge(Known()).Reason.ShouldBe(DisabledReason.CircuitBreaker);

        Record.HasUnresolved.ShouldBeTrue();
        m.Availability.ShouldBe(TrackerAvailability.Paused);
    }

    [Fact]
    public void NFR01_Q3_Otherwise_a_wedge_quarantines_with_no_worker_from_Running_or_Recovering()
    {
        var m = Running();
        m.ShellGone();
        m.State.ShouldBe(TrackerState.Recovering);

        m.OnWedge(Known()).Outcome.ShouldBe(WedgeOutcome.Quarantined);

        m.State.ShouldBe(TrackerState.Quarantined);
        m.Phase.ShouldBe(QuarantinePhase.Waiting);
        m.LiveWorkers.ShouldBe(0);
        _pub.Current.IsInactive.ShouldBeTrue();
    }

    // ---- waiting ----------------------------------------------------------------------------------------

    [Fact]
    public void NFR01_Q4_When_every_entry_resolves_one_normal_worker_starts_and_the_record_is_cleared()
    {
        var z = new FakeZombie();
        var m = Quarantined(z);
        z.Exit();

        Tick(m).ShouldBe(QuarantineAction.StartWorker);

        m.State.ShouldBe(TrackerState.Running);
        m.LiveWorkers.ShouldBe(1);
        _guard.LiveThreads(TrackerThreadKind.Worker).ShouldBe(1);
        Record.HasUnresolved.ShouldBeFalse();
    }

    [Fact]
    public void NFR01_Q5_A_complete_hold_grants_one_automatic_trial_that_does_not_clear_the_record()
    {
        var m = Quarantined();

        TickFor(m, 29).ShouldBe(QuarantineAction.None);         // the 30 s minimum is not over
        TickFor(m, 5).ShouldBe(QuarantineAction.StartTrial);

        m.Phase.ShouldBe(QuarantinePhase.Trial);
        m.TrialKind.ShouldBe(TrialKind.Automatic);
        m.LiveWorkers.ShouldBe(1);
        Record.HasUnresolved.ShouldBeTrue();
        m.Availability.ShouldBe(TrackerAvailability.RecoveryAttempt);
    }

    [Fact]
    public void NFR01_Q6_When_every_suspect_was_tried_a_complete_hold_starts_nothing()
    {
        var m = Quarantined();
        TickFor(m, 40).ShouldBe(QuarantineAction.StartTrial);
        _clock.Advance(TimeSpan.FromSeconds(10));
        Tick(m).ShouldBe(QuarantineAction.StopTrial);           // inconclusive
        m.TrialStopped();

        TickFor(m, 600).ShouldBe(QuarantineAction.None);        // ten minutes: no automatic worker against the same server

        m.Phase.ShouldBe(QuarantinePhase.Waiting);
        m.LiveWorkers.ShouldBe(0);
    }

    [Fact]
    public void NFR01_Q7_Hang_evidence_restarts_the_hold()
    {
        var m = Quarantined();
        TickFor(m, 25);
        _probe.Hang(42);
        Tick(m).ShouldBe(QuarantineAction.None);
        _probe.Hang(42, false);

        TickFor(m, 9).ShouldBe(QuarantineAction.None);          // 9 s clean after the hang: not yet
        TickFor(m, 3).ShouldBe(QuarantineAction.StartTrial);
    }

    // ---- trial ------------------------------------------------------------------------------------------

    private TrackerStateMachine InTrial()
    {
        var m = Quarantined();
        TickFor(m, 40).ShouldBe(QuarantineAction.StartTrial);
        return m;
    }

    [Fact]
    public void NFR01_Q8_A_trial_with_a_full_enumeration_passes_only_at_its_deadline_and_clears_the_record()
    {
        var m = InTrial();
        _evidence++;                                            // the trial connected and enumerated

        _clock.Advance(TimeSpan.FromSeconds(9));
        Tick(m).ShouldBe(QuarantineAction.None);
        m.Phase.ShouldBe(QuarantinePhase.Trial);                // not before the deadline
        _clock.Advance(TimeSpan.FromSeconds(1));
        Tick(m).ShouldBe(QuarantineAction.None);

        m.State.ShouldBe(TrackerState.Running);
        m.LiveWorkers.ShouldBe(1);                              // the trial worker continues
        Record.HasUnresolved.ShouldBeFalse();
        Record.Releases.ByTrial.ShouldBe(1);
    }

    [Fact]
    public void NFR01_Q9_A_trial_without_a_full_enumeration_is_stopped_and_returns_to_waiting()
    {
        var m = InTrial();
        int gen = _pub.Generation;
        _clock.Advance(TimeSpan.FromSeconds(10));

        Tick(m).ShouldBe(QuarantineAction.StopTrial);
        m.TrialStopped();

        m.Phase.ShouldBe(QuarantinePhase.Waiting);
        m.LiveWorkers.ShouldBe(0);
        _guard.LiveThreads(TrackerThreadKind.Worker).ShouldBe(0);
        _guard.AbandonedThreads.ShouldBe(1);                    // a clean stop is not an abandonment
        _pub.Generation.ShouldBe(gen + 1);                      // the trial's generation can no longer publish
        _pub.Current.IsInactive.ShouldBeTrue();
        Record.HasUnresolved.ShouldBeTrue();
    }

    [Fact]
    public void NFR01_Q10_A_wedging_trial_returns_to_quarantine_counted_and_the_same_server_gets_no_second_automatic_trial()
    {
        var m = InTrial();

        m.OnWedge(Known()).Outcome.ShouldBe(WedgeOutcome.Quarantined);   // repeated failure, same server

        m.Phase.ShouldBe(QuarantinePhase.Waiting);
        _guard.AbandonedThreads.ShouldBe(2);
        m.WedgesInWindow.ShouldBe(2);
        TickFor(m, 600).ShouldBe(QuarantineAction.None);
    }

    [Fact]
    public void NFR01_Q10_A_trial_that_wedges_on_a_different_server_earns_one_more_trial_after_a_new_full_hold()
    {
        var m = InTrial();
        var other = _probe.Run(43);

        m.OnWedge(Known(server: other));

        TickFor(m, 29).ShouldBe(QuarantineAction.None);          // a complete new hold and minimum first
        TickFor(m, 5).ShouldBe(QuarantineAction.StartTrial);
        m.OnWedge(Known(server: other)).Outcome.ShouldBe(WedgeOutcome.Disabled);   // third wedge in the window: breaker
        m.DisabledReason.ShouldBe(DisabledReason.CircuitBreaker);
    }

    [Fact]
    public void NFR01_Q11_Evidence_during_a_trial_ends_the_quarantine_at_once()
    {
        var z = new FakeZombie();
        var m = Quarantined(z);
        TickFor(m, 40).ShouldBe(QuarantineAction.StartTrial);

        z.Exit();
        Tick(m).ShouldBe(QuarantineAction.None);

        m.State.ShouldBe(TrackerState.Running);
        m.LiveWorkers.ShouldBe(1);
        Record.HasUnresolved.ShouldBeFalse();
    }

    // ---- delayed completion, replacement ------------------------------------------------------------------

    [Fact]
    public void NFR01_Q12_A_zombie_completing_late_resolves_only_its_own_entry()
    {
        var z1 = new FakeZombie();
        var z2 = new FakeZombie();
        var m = Quarantined(z1);
        TickFor(m, 40).ShouldBe(QuarantineAction.StartTrial);
        m.OnWedge(Known(z2));

        z1.Exit();
        Tick(m).ShouldBe(QuarantineAction.None);                // z2 is still blocked
        Record.Entries.Count.ShouldBe(1);

        z2.Exit();
        Tick(m).ShouldBe(QuarantineAction.StartWorker);
        _guard.AbandonedThreads.ShouldBe(2);                    // late completions never refund the cap
    }

    [Fact]
    public void NFR01_Q13_A_shell_restart_resolves_a_shell_suspect()
    {
        var shell = _probe.Run(500, ownsFrame: false);
        var m = Running();
        m.OnWedge(new QuarantineEntry(new FakeZombie(), TestEntries.ActiveSnapshot(CallSite.RootCreate, 500), Suspect.KnownServer(shell), null, _clock.GetTimestamp()));

        _probe.Exit(500);
        _probe.Run(500, ownsFrame: false);                      // Windows restarts the shell, even with the same pid

        Tick(m).ShouldBe(QuarantineAction.StartWorker);
    }

    [Fact]
    public void NFR01_Q14_Resolution_while_Disabled_clears_the_record_but_never_restarts_by_itself()
    {
        var z = new FakeZombie();
        var m = Running(new TrackerLimits { BreakerWedges = 1 });
        m.OnWedge(Known(z));
        z.Exit();

        Tick(m).ShouldBe(QuarantineAction.None);

        m.State.ShouldBe(TrackerState.Disabled);
        Record.HasUnresolved.ShouldBeFalse();
        m.Retry().ShouldBe(RetryResult.Started);                // Q23 afterwards
    }

    // ---- stop / start -------------------------------------------------------------------------------------

    [Fact]
    public void NFR01_Q15_Stop_while_waiting_keeps_the_record()
    {
        var m = Quarantined();

        m.Stop();

        m.State.ShouldBe(TrackerState.Stopped);
        Record.HasUnresolved.ShouldBeTrue();
    }

    [Fact]
    public void NFR01_Q16_Stop_during_a_trial_spends_it_and_frees_the_slot()
    {
        var m = InTrial();

        m.Stop();

        _guard.LiveThreads(TrackerThreadKind.Worker).ShouldBe(0);
        m.Start().ShouldBe(StartResult.Quarantined);
        TickFor(m, 600).ShouldBe(QuarantineAction.None);         // the stopped trial counted: no new automatic one
    }

    [Fact]
    public void NFR01_Q17_Start_or_a_new_instance_meets_the_record_and_needs_a_full_hold_again()
    {
        var m = Quarantined();
        TickFor(m, 25).ShouldBe(QuarantineAction.None);         // a clean hold, but the 30 s minimum is not over...
        m.Stop();                                               // ...and the tracker is stopped before any trial

        var fresh = Machine();                                  // a new tracker object in the same process
        fresh.Start().ShouldBe(StartResult.Quarantined);

        fresh.LiveWorkers.ShouldBe(0);
        Tick(fresh).ShouldBe(QuarantineAction.None);            // the hold starts over after a Start
        TickFor(fresh, 12).ShouldBe(QuarantineAction.StartTrial);
    }

    [Fact]
    public void NFR01_Q18_Start_with_an_empty_record_runs_normally()
    {
        var z = new FakeZombie();
        var m = Quarantined(z);
        m.Stop();
        z.Exit();
        Record.Assess(_probe);                                  // the supervisor's check just before Start

        m.Start().ShouldBe(StartResult.Started);
        m.State.ShouldBe(TrackerState.Running);
    }

    // ---- retry --------------------------------------------------------------------------------------------

    [Fact]
    public void NFR01_Q19_Retry_while_running_recovering_or_stopped_does_nothing()
    {
        Machine().Retry().ShouldBe(RetryResult.NotDisabled);
        var m = Running();
        m.Retry().ShouldBe(RetryResult.NotDisabled);
        m.ShellGone();
        m.Retry().ShouldBe(RetryResult.NotDisabled);
    }

    [Fact]
    public void NFR01_Q20_Retry_during_a_trial_is_refused()
    {
        InTrial().Retry().ShouldBe(RetryResult.TrialInProgress);
    }

    [Fact]
    public void NFR01_Q21_Retry_while_waiting_is_refused_on_hang_evidence_and_otherwise_an_explicit_trial()
    {
        var m = Quarantined();

        m.Retry(hangEvidence: true).ShouldBe(RetryResult.ServerNotResponding);
        m.LiveWorkers.ShouldBe(0);

        m.Retry(hangEvidence: false).ShouldBe(RetryResult.TrialStarted);   // no hold, no 30 s minimum for an explicit trial
        m.TrialKind.ShouldBe(TrialKind.Explicit);
        Record.HasUnresolved.ShouldBeTrue();
    }

    [Fact]
    public void NFR01_Q22_Retry_after_the_breaker_respects_quarantine()
    {
        var m = Running(new TrackerLimits { BreakerWedges = 1 });
        m.OnWedge(Known());

        m.Retry(hangEvidence: true).ShouldBe(RetryResult.ServerNotResponding);
        m.State.ShouldBe(TrackerState.Disabled);
        m.WedgesInWindow.ShouldBe(1);                           // the window is kept on a refusal

        m.Retry(hangEvidence: false).ShouldBe(RetryResult.TrialStarted);
        m.State.ShouldBe(TrackerState.Quarantined);
        m.Phase.ShouldBe(QuarantinePhase.Trial);
        m.WedgesInWindow.ShouldBe(0);
    }

    [Fact]
    public void NFR01_Q23_Retry_after_the_breaker_with_an_empty_record_restarts_normally()
    {
        var z = new FakeZombie();
        var m = Running(new TrackerLimits { BreakerWedges = 1 });
        m.OnWedge(Known(z));
        z.Exit();
        Record.Assess(_probe);

        m.Retry().ShouldBe(RetryResult.Started);
        m.State.ShouldBe(TrackerState.Running);
    }

    [Fact]
    public void NFR01_Q24_Retry_at_the_cap_is_refused_in_every_state()
    {
        var m = Running(new TrackerLimits { MaxAbandonedThreads = 1 });
        m.OnWedge(Known());

        m.Retry().ShouldBe(RetryResult.ThreadCapReached);
        m.Retry(hangEvidence: false).ShouldBe(RetryResult.ThreadCapReached);
        m.CanRetry.ShouldBeFalse();
    }
}

/// <summary>
/// 04 § M.8: a seeded random model of the machine + record. Whatever happens, the bounds hold after every step: at most one
/// live worker, the abandoned count ≤ cap and never decreasing, and no automatic trial twice against one server per episode.
/// </summary>
[Trait("Kind", "Deterministic")]
public class QuarantineBoundsModelTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(42)]
    [InlineData(2026)]
    public void NFR01_M8_Random_event_sequences_never_break_the_bounds(int seed)
    {
        var rng = new Random(seed);
        var clock = new FakeTimeProvider();
        var probe = new FakeServerProbe();
        var guard = new TrackerProcessGuard(quarantine: new QuarantineRecord(clock));
        var pub = new StatePublisher(_ => { });
        var m = new TrackerStateMachine(clock, pub, guard);
        var servers = new List<ServerIdentity> { probe.Run(10), probe.Run(11), probe.Run(12) };
        var zombies = new List<FakeZombie>();
        long evidence = 0;
        int lastAbandoned = 0;
        int automaticTrials = 0;
        var triedThisEpisode = new List<Suspect>();
        m.Start();

        for (int step = 0; step < 3000; step++)
        {
            switch (rng.Next(9))
            {
                case 0 or 1:                                        // the live worker wedges
                    if (m.LiveWorkers == 1)
                    {
                        var z = new FakeZombie();
                        zombies.Add(z);
                        var s = servers[rng.Next(servers.Count)];
                        m.OnWedge(rng.Next(4) == 0 ? TestEntries.Unknown(z, clock) : TestEntries.Known(z, s, clock));
                    }

                    break;
                case 2:                                             // a zombie returns late
                    if (zombies.Count > 0)
                    {
                        zombies[rng.Next(zombies.Count)].Exit();
                    }

                    break;
                case 3:                                             // a server exits or restarts (pid reuse)
                    int i = rng.Next(servers.Count);
                    probe.Exit(servers[i].Pid);
                    servers[i] = probe.Run(servers[i].Pid);
                    break;
                case 4:
                    probe.Hang(servers[rng.Next(servers.Count)].Pid, rng.Next(2) == 0);
                    break;
                case 5:                                             // the user
                    if (rng.Next(3) == 0)
                    {
                        m.Stop(workerAbandoned: rng.Next(5) == 0);
                        if (guard.Quarantine.HasUnresolved)
                        {
                            guard.Quarantine.Assess(probe);
                        }

                        m.Start();
                    }
                    else
                    {
                        m.Retry(guard.Quarantine.HasUnresolved && guard.Quarantine.HangEvidenceNow(probe), evidence);
                    }

                    break;
                case 6:                                             // a worker enumerates successfully
                    if (m.LiveWorkers == 1 && rng.Next(2) == 0)
                    {
                        evidence++;
                    }

                    break;
                default:                                            // the monitor, one second later
                    bool wasWaiting = m.State == TrackerState.Quarantined && m.Phase == QuarantinePhase.Waiting;
                    var action = m.OnQuarantineTick(guard.Quarantine.HasUnresolved ? guard.Quarantine.Assess(probe) : new QuarantineAssessment(true, false, false, false), evidence);
                    if (action == QuarantineAction.StopTrial)
                    {
                        if (rng.Next(4) == 0)
                        {
                            m.OnWedge(TestEntries.Unknown(new FakeZombie(), clock));   // did not stop in time
                        }
                        else
                        {
                            m.TrialStopped();
                        }
                    }

                    if (action == QuarantineAction.StartTrial)
                    {
                        var before = guard.Quarantine.Entries.Select(e => e.Suspect).ToList();   // what the trial was granted for
                        wasWaiting.ShouldBeTrue();
                        automaticTrials++;
                        // the trial is for a suspect that had none yet in this episode
                        before.Any(s => !triedThisEpisode.Any(t => t.SameServerAs(s))).ShouldBeTrue($"seed {seed} step {step}: automatic trial against a tried server");
                        triedThisEpisode.AddRange(before);
                    }

                    clock.Advance(TimeSpan.FromSeconds(1));
                    break;
            }

            if (!guard.Quarantine.HasUnresolved)
            {
                triedThisEpisode.Clear();                           // the record emptied: a new episode
            }

            // M.8 after every step
            m.LiveWorkers.ShouldBeInRange(0, 1);
            guard.LiveThreads(TrackerThreadKind.Worker).ShouldBe(m.LiveWorkers, $"seed {seed} step {step}");
            guard.AbandonedThreads.ShouldBeLessThanOrEqualTo(TrackerLimits.Default.MaxAbandonedThreads);
            guard.AbandonedThreads.ShouldBeGreaterThanOrEqualTo(lastAbandoned);
            lastAbandoned = guard.AbandonedThreads;
            if (m.State == TrackerState.Quarantined && m.Phase == QuarantinePhase.Waiting)
            {
                m.LiveWorkers.ShouldBe(0);
            }
        }

        automaticTrials.ShouldBeLessThanOrEqualTo(guard.AbandonedThreads);   // each automatic trial needs a new suspect, i.e. a new wedge
    }
}
