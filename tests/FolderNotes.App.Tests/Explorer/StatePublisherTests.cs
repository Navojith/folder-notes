using FolderNotes.App.Explorer;

namespace FolderNotes.App.Tests.Explorer;

/// <summary>04 D.6, D.7b, B.6: ordering, no-op suppression and abandoned-worker publication (REQ-031, NFR-01).</summary>
public class StatePublisherTests
{
    private readonly List<ActiveFolderState> _delivered = [];
    private readonly StatePublisher _pub;

    public StatePublisherTests() => _pub = new StatePublisher(_delivered.Add);

    private static ActiveFolderState S(nint frame, string? path, nint tab = 1) => new(frame, tab, path, 0);

    [Fact]
    public void REQ031_A_new_folder_is_published_with_the_sequence_of_its_evaluation()
    {
        long seq = _pub.NextSequence();

        _pub.TryPublish(_pub.Generation, seq, S(1, @"C:\a")).ShouldBe(PublishResult.Published);

        _delivered.ShouldHaveSingleItem().ShouldBe(new ActiveFolderState(1, 1, @"C:\a", seq));
        _pub.Current.Path.ShouldBe(@"C:\a");
    }

    [Fact]
    public void REQ031_An_unchanged_folder_is_not_delivered_again()
    {
        Publish(S(1, @"C:\a"));

        Publish(S(1, @"C:\a")).ShouldBe(PublishResult.Unchanged);
        Publish(S(1, @"C:\A")).ShouldBe(PublishResult.Unchanged);               // path compares ignoring case
        Publish(S(1, @"C:\a", tab: 7)).ShouldBe(PublishResult.Unchanged);       // another tab, same folder

        _delivered.Count.ShouldBe(1);
    }

    [Fact]
    public void REQ031_The_same_path_in_another_frame_is_a_change()
    {
        Publish(S(1, @"C:\a"));
        Publish(S(2, @"C:\a")).ShouldBe(PublishResult.Published);
        _delivered.Count.ShouldBe(2);
    }

    [Fact]
    public void REQ031_A_virtual_folder_is_published_as_a_null_path_once()
    {
        Publish(S(1, @"C:\a"));
        Publish(S(1, null)).ShouldBe(PublishResult.Published);
        Publish(S(1, null)).ShouldBe(PublishResult.Unchanged);
        _delivered[^1].Path.ShouldBeNull();
    }

    [Fact]
    public void REQ031_Inactive_from_the_start_is_not_delivered()
    {
        Publish(ActiveFolderState.Inactive()).ShouldBe(PublishResult.Unchanged);
        _delivered.ShouldBeEmpty();
    }

    [Fact]
    public void REQ031_An_older_result_finishing_late_can_never_overwrite_a_newer_one()
    {
        long older = _pub.NextSequence();
        long newer = _pub.NextSequence();

        _pub.TryPublish(_pub.Generation, newer, S(1, @"C:\new")).ShouldBe(PublishResult.Published);
        _pub.TryPublish(_pub.Generation, older, S(1, @"C:\old")).ShouldBe(PublishResult.StaleSequence);

        _pub.Current.Path.ShouldBe(@"C:\new");
        _delivered.Select(d => d.Path).ShouldBe([@"C:\new"]);
    }

    [Fact]
    public void REQ031_An_older_result_is_dropped_even_when_the_newer_one_was_an_unchanged_no_op()
    {
        Publish(S(1, @"C:\a"));
        long older = _pub.NextSequence();
        long newer = _pub.NextSequence();
        _pub.TryPublish(_pub.Generation, newer, S(1, @"C:\a")).ShouldBe(PublishResult.Unchanged);

        _pub.TryPublish(_pub.Generation, older, S(1, @"C:\b")).ShouldBe(PublishResult.StaleSequence);

        _pub.Current.Path.ShouldBe(@"C:\a");
    }

    [Fact]
    public void REQ031_An_older_result_is_dropped_even_when_the_newer_evaluation_produced_no_information()
    {
        long older = _pub.NextSequence();
        long newer = _pub.NextSequence();
        _pub.TryPublish(_pub.Generation, newer, null).ShouldBe(PublishResult.Unchanged);   // a transient failure

        _pub.TryPublish(_pub.Generation, older, S(1, @"C:\b")).ShouldBe(PublishResult.StaleSequence);
        _delivered.ShouldBeEmpty();
    }

    [Fact]
    public void REQ031_The_same_sequence_cannot_be_published_twice()
    {
        long seq = _pub.NextSequence();
        _pub.TryPublish(_pub.Generation, seq, S(1, @"C:\a")).ShouldBe(PublishResult.Published);
        _pub.TryPublish(_pub.Generation, seq, S(1, @"C:\b")).ShouldBe(PublishResult.StaleSequence);
    }

    [Fact]
    public void REQ031_Delivered_sequences_strictly_increase_under_a_shuffled_arrival_order()
    {
        var rng = new Random(11);
        var seqs = Enumerable.Range(0, 200).Select(_ => _pub.NextSequence()).ToList();
        var order = seqs.OrderBy(_ => rng.Next()).ToList();

        foreach (long s in order)
        {
            _pub.TryPublish(_pub.Generation, s, S(1, $@"C:\f{s}"));
        }

        _delivered.Select(d => d.Sequence).ShouldBe(_delivered.Select(d => d.Sequence).OrderBy(x => x));
        _pub.Current.Sequence.ShouldBe(_delivered[^1].Sequence);
        // whatever arrived last-by-sequence among those accepted wins; nothing older ever replaces something newer
        _delivered.Count.ShouldBeGreaterThan(0);
    }

    // ---- abandoned workers ----------------------------------------------------------------------

    [Fact]
    public void NFR01_Abandon_publishes_Inactive_and_bumps_the_generation()
    {
        Publish(S(1, @"C:\a"));
        int old = _pub.Generation;

        int fresh = _pub.Abandon();

        fresh.ShouldBe(old + 1);
        _pub.Generation.ShouldBe(fresh);
        _delivered[^1].IsInactive.ShouldBeTrue();
        _pub.Current.IsInactive.ShouldBeTrue();
    }

    [Fact]
    public void NFR01_An_abandoned_worker_can_never_publish_even_with_a_fresh_sequence()
    {
        int oldGeneration = _pub.Generation;
        _pub.Abandon();
        int delivered = _delivered.Count;

        _pub.TryPublish(oldGeneration, _pub.NextSequence(), S(1, @"C:\zombie")).ShouldBe(PublishResult.StaleGeneration);

        _delivered.Count.ShouldBe(delivered);
        _pub.Current.IsInactive.ShouldBeTrue();
    }

    [Fact]
    public void NFR01_The_replacement_generation_publishes_normally_after_an_abandon()
    {
        Publish(S(1, @"C:\a"));
        int fresh = _pub.Abandon();

        _pub.TryPublish(fresh, _pub.NextSequence(), S(1, @"C:\a")).ShouldBe(PublishResult.Published);   // same folder as before the wedge: shown again
        _delivered.Count.ShouldBe(3);                                                                   // a, inactive, a
    }

    [Fact]
    public void NFR01_Abandoning_when_already_Inactive_delivers_nothing_extra()
    {
        _pub.Abandon();
        _pub.Abandon();
        _delivered.ShouldBeEmpty();
    }

    [Fact]
    public void NFR01_Stale_results_arriving_after_an_abandon_with_older_sequences_stay_stale_for_the_new_generation()
    {
        long inFlight = _pub.NextSequence();          // taken by the doomed worker
        int fresh = _pub.Abandon();                   // Inactive gets a newer sequence

        _pub.TryPublish(fresh, inFlight, S(1, @"C:\late")).ShouldBe(PublishResult.StaleSequence);
    }

    [Fact]
    public async Task NFR01_No_delivery_from_an_old_generation_happens_after_Abandon_returns_under_concurrency()
    {
        var log = new List<(int Seq, string? Path)>();
        var gate = new object();
        StatePublisher? pub = null;
        int abandonedAtCount = -1;
        pub = new StatePublisher(s => { lock (gate) { log.Add(((int)s.Sequence, s.Path)); } });

        int oldGen = pub.Generation;
        using var go = new ManualResetEventSlim();
        var workers = Enumerable.Range(0, 4).Select(w => Task.Run(() =>
        {
            go.Wait();
            for (int i = 0; i < 5000; i++)
            {
                pub.TryPublish(oldGen, pub.NextSequence(), new ActiveFolderState(1 + w, 1, $@"C:\w{w}\{i}", 0));
            }
        })).ToArray();

        go.Set();
        for (int spin = 0; spin < 2000; spin++)       // wait until the workers really are publishing
        {
            lock (gate) { if (log.Count > 5) { break; } }
            await Task.Delay(1, TestContext.Current.CancellationToken);
        }

        pub.Abandon();
        lock (gate) { abandonedAtCount = log.Count; }
        await Task.WhenAll(workers);

        lock (gate)
        {
            log.Count.ShouldBe(abandonedAtCount, "nothing from the abandoned generation may be delivered after Abandon() returned");
            log.Count.ShouldBeGreaterThan(5);
            log[^1].Path.ShouldBeNull();   // the last delivery is the Inactive
        }
    }

    private PublishResult Publish(ActiveFolderState s) => _pub.TryPublish(_pub.Generation, _pub.NextSequence(), s);
}
