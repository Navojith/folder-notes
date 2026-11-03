using FolderNotes.App.Explorer;
using Microsoft.Extensions.Time.Testing;

namespace FolderNotes.App.Tests.Explorer;

/// <summary>04 § M.2: the call journal. A record is active only while its calls are in flight, and a torn, stale or dangling record is never read as active.</summary>
[Trait("Kind", "Deterministic")]
public class CallJournalTests
{
    private readonly FakeTimeProvider _clock = new();

    private CallJournal Journal(int generation = 7) => new(generation, _clock);

    [Fact]
    public void NFR01_M2_An_open_operation_is_active_with_everything_it_recorded()
    {
        var j = Journal();
        long started = _clock.GetTimestamp();

        long id = j.Open(CallSite.PathRead, 4242, 99);
        var snap = j.Read(started);

        snap.Kind.ShouldBe(JournalReadKind.Active);
        snap.Record.ShouldBe(new CallRecord(7, id, CallSite.PathRead, 4242, 99, -1, started));
    }

    [Fact]
    public void NFR01_M2_The_record_is_cleared_only_when_the_operation_closes()
    {
        var j = Journal();
        long id = j.Open(CallSite.Enumerate, 500);
        _clock.Advance(TimeSpan.FromSeconds(30));             // however long the call takes, the record stays
        j.Read().IsActive.ShouldBeTrue();
        j.HasCompleted(id).ShouldBeFalse();

        j.Close(id);

        j.Read().Kind.ShouldBe(JournalReadKind.Idle);
        j.HasCompleted(id).ShouldBeTrue();
    }

    [Fact]
    public void NFR01_M2_The_innermost_operation_is_reported_and_the_outer_one_returns_after_it()
    {
        var j = Journal();
        long outer = j.Open(CallSite.Enumerate, 500);
        long inner = j.Open(CallSite.ItemProbe, itemIndex: 3);

        j.Read().Record.Site.ShouldBe(CallSite.ItemProbe);
        j.Read().Record.ItemIndex.ShouldBe(3);
        j.Read().Record.TargetPid.ShouldBe(0u);               // the item's process is not known before get_HWND

        j.Close(inner);
        j.Read().Record.OperationId.ShouldBe(outer);
        j.HasCompleted(inner).ShouldBeTrue();
        j.HasCompleted(outer).ShouldBeFalse();
    }

    [Fact]
    public void NFR01_M2_Operation_ids_are_never_reused_so_a_later_operation_does_not_hide_an_earlier_completion()
    {
        var j = Journal();
        long first = j.Open(CallSite.PathRead, 1);
        j.Close(first);
        long second = j.Open(CallSite.PathRead, 1);

        second.ShouldBeGreaterThan(first);
        j.HasCompleted(first).ShouldBeTrue();
        j.HasCompleted(second).ShouldBeFalse();
        j.HasCompleted(second + 1).ShouldBeFalse();             // an id that was never handed out is not "completed"
        j.HasCompleted(0).ShouldBeFalse();
    }

    [Fact]
    public void NFR01_M2_A_record_read_while_it_is_being_written_is_indeterminate_not_active()
    {
        var j = Journal();
        JournalSnapshot? midWrite = null;
        bool? completedMidWrite = null;
        long first = j.Open(CallSite.PathRead, 1);
        j.Close(first);
        j.WhileWriting = () =>
        {
            j.WhileWriting = null;                              // only the next write
            midWrite = j.Read();
            completedMidWrite = j.HasCompleted(first);
        };

        j.Open(CallSite.PathRead, 4242);

        midWrite!.Value.Kind.ShouldBe(JournalReadKind.Indeterminate);
        completedMidWrite.ShouldBe(false);                      // a torn read never answers "completed"
        j.Read().Kind.ShouldBe(JournalReadKind.Active);         // once written, the record is readable
    }

    [Fact]
    public void NFR01_M2_A_record_older_than_the_timed_evaluation_is_stale()
    {
        var j = Journal();
        j.Open(CallSite.PathRead, 4242);
        _clock.Advance(TimeSpan.FromMilliseconds(5));

        j.Read(evaluationStartedAt: _clock.GetTimestamp()).Kind.ShouldBe(JournalReadKind.Stale);
    }

    [Fact]
    public void NFR01_M2_An_operation_left_open_is_closed_and_counted_at_the_next_evaluation()
    {
        var j = Journal();
        long dangling = j.Open(CallSite.Release);

        j.BeginEvaluation();

        j.Defects.ShouldBe(1);
        j.Read().Kind.ShouldBe(JournalReadKind.Idle);
        j.HasCompleted(dangling).ShouldBeTrue();
        j.BeginEvaluation();
        j.Defects.ShouldBe(1);                                  // a clean start is not a defect
    }

    [Fact]
    public void NFR01_M2_Nesting_deeper_than_two_is_a_defect_and_never_loses_the_outer_operation()
    {
        var j = Journal();
        long outer = j.Open(CallSite.Enumerate, 500);
        j.Open(CallSite.ItemProbe, itemIndex: 0);
        long third = j.Open(CallSite.Release);

        j.Defects.ShouldBe(1);
        j.Read().Record.OperationId.ShouldBe(third);
        j.Close(third);
        j.Read().Record.OperationId.ShouldBe(outer);
    }

    [Fact]
    public void NFR01_M2_A_scope_closes_its_operation_even_when_the_call_throws()
    {
        var j = Journal();
        long id = 0;

        Should.Throw<InvalidOperationException>(() =>
        {
            using var op = j.Scope(CallSite.PathRead, 1);
            id = op.OperationId;
            throw new InvalidOperationException("call failed");
        });

        j.HasCompleted(id).ShouldBeTrue();
        j.Read().Kind.ShouldBe(JournalReadKind.Idle);
    }

    [Fact]
    [Trait("Kind", "Timing")]
    public void NFR01_M2_Concurrent_reads_never_see_a_torn_active_record()
    {
        var j = new CallJournal(3);
        using var stop = new CancellationTokenSource();
        var writer = new Thread(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                // Each record is internally consistent: pid, frame and item index are all derived from the id.
                long next = j.LastOperationId + 1;
                long id = j.Open(CallSite.PathRead, (uint)next, (nint)next, (int)next);
                j.Close(id);
            }
        }) { IsBackground = true };
        writer.Start();

        int active = 0;
        for (int i = 0; i < 200_000; i++)
        {
            var snap = j.Read();
            if (snap.IsActive)
            {
                active++;
                var r = snap.Record;
                ((long)r.TargetPid).ShouldBe(r.OperationId);
                ((long)r.FrameHwnd).ShouldBe(r.OperationId);
                ((long)r.ItemIndex).ShouldBe(r.OperationId);
                r.Generation.ShouldBe(3);
            }
        }

        stop.Cancel();
        writer.Join();
        active.ShouldBeGreaterThan(0);                          // the test did observe live records
    }
}
