using FolderNotes.App.Explorer;

namespace FolderNotes.App.Tests.Explorer;

/// <summary>A disposable stand-in for a COM reference holder; counts live instances and double releases.</summary>
internal sealed class LiveCounter
{
    private int _live;

    public int Live => Volatile.Read(ref _live);

    internal void Add(int n) => Interlocked.Add(ref _live, n);
}

internal sealed class FakeHandle : IDisposable
{
    private readonly LiveCounter? _counter;
    private int _disposals;

    public FakeHandle(LiveCounter? counter = null)
    {
        _counter = counter;
        _counter?.Add(1);
    }

    public int Disposals => _disposals;

    public bool IsDisposed => _disposals > 0;

    public void Dispose()
    {
        if (Interlocked.Increment(ref _disposals) == 1)
        {
            _counter?.Add(-1);
        }
    }
}

/// <summary>04 § C: the browser→tab cache (REQ-034, NFR-01 prompt release). Tests that count live handles use deltas.</summary>
public class TabMapTests
{
    private static DiscoveredTab<FakeHandle> Tab(nint frame, nint tab) => new(frame, tab, new FakeHandle());

    [Fact]
    public void REQ034_Reconcile_adds_tabs_and_finds_them_by_tab_key()
    {
        using var map = new TabMap<FakeHandle>();
        var r = map.Reconcile([Tab(1, 11), Tab(1, 12), Tab(2, 21)], foregroundFrame: 1);

        r.ShouldBe(new ReconcileResult(3, 0, 0, 0));
        map.Count.ShouldBe(3);
        map.TryGet(12, out var e).ShouldBeTrue();
        e.FrameHwnd.ShouldBe((nint)1);
        map.TryGet(99, out _).ShouldBeFalse();
        map.TabsOf(1).OrderBy(x => x).ToArray().ShouldBe([(nint)11, 12]);
    }

    [Fact]
    public void REQ034_Reconcile_replaces_the_handle_of_a_known_tab_and_releases_the_old_one_once()
    {
        using var map = new TabMap<FakeHandle>();
        var first = Tab(1, 11);
        map.Reconcile([first], 1);
        map.RecordSuccess(11, @"C:\a");

        var second = Tab(1, 11);
        var r = map.Reconcile([second], 1);

        r.Replaced.ShouldBe(1);
        first.Handle.Disposals.ShouldBe(1);
        second.Handle.IsDisposed.ShouldBeFalse();
        map.TryGet(11, out var e).ShouldBeTrue();
        e.Handle.ShouldBeSameAs(second.Handle);
        e.LastPath.ShouldBe(@"C:\a");   // what we last read survives a rebuild
    }

    [Fact]
    public void REQ034_Reconcile_removes_tabs_that_are_no_longer_listed()
    {
        using var map = new TabMap<FakeHandle>();
        var gone = Tab(1, 11);
        map.Reconcile([gone, Tab(1, 12)], 1);

        var r = map.Reconcile([Tab(1, 12)], 1);

        r.Removed.ShouldBe(1);
        map.Contains(11).ShouldBeFalse();
        gone.Handle.Disposals.ShouldBe(1);
    }

    [Fact]
    public void REQ034_A_tab_listed_twice_keeps_the_first_and_releases_the_duplicate()
    {
        using var map = new TabMap<FakeHandle>();
        var a = Tab(1, 11);
        var b = Tab(1, 11);
        map.Reconcile([a, b], 1);

        map.Count.ShouldBe(1);
        a.Handle.IsDisposed.ShouldBeFalse();
        b.Handle.Disposals.ShouldBe(1);
    }

    [Fact]
    public void REQ034_A_tab_that_moves_to_another_frame_follows_it()
    {
        using var map = new TabMap<FakeHandle>();
        map.Reconcile([Tab(1, 11)], 1);
        map.Reconcile([Tab(2, 11)], 2);

        map.TryGet(11, out var e).ShouldBeTrue();
        e.FrameHwnd.ShouldBe((nint)2);
        map.TabsOf(1).ShouldBeEmpty();
    }

    [Fact]
    public void REQ034_RemoveFrame_drops_all_its_tabs_and_only_those()
    {
        using var map = new TabMap<FakeHandle>();
        var t1 = Tab(1, 11); var t2 = Tab(1, 12); var t3 = Tab(2, 21);
        map.Reconcile([t1, t2, t3], 1);

        map.RemoveFrame(1).ShouldBe(2);

        map.Count.ShouldBe(1);
        t1.Handle.Disposals.ShouldBe(1);
        t2.Handle.Disposals.ShouldBe(1);
        t3.Handle.IsDisposed.ShouldBeFalse();
        map.RemoveFrame(1).ShouldBe(0);
    }

    [Fact]
    public void REQ034_Remove_reports_whether_it_removed_and_releases_once()
    {
        using var map = new TabMap<FakeHandle>();
        var t = Tab(1, 11);
        map.Reconcile([t], 1);

        map.Remove(11).ShouldBeTrue();
        map.Remove(11).ShouldBeFalse();
        t.Handle.Disposals.ShouldBe(1);
    }

    [Fact]
    public void REQ034_SweepDead_drops_entries_whose_window_is_gone_a_missed_DESTROY()
    {
        using var map = new TabMap<FakeHandle>();
        var t = Tab(1, 11);
        map.Reconcile([t, Tab(1, 12)], 1);

        map.SweepDead(hwnd => hwnd != 11).ShouldBe(1);

        map.Contains(11).ShouldBeFalse();
        map.Contains(12).ShouldBeTrue();
        t.Handle.Disposals.ShouldBe(1);
    }

    [Fact]
    public void REQ031_Gone_drops_only_that_entry()
    {
        using var map = new TabMap<FakeHandle>();
        var t = Tab(1, 11);
        map.Reconcile([t, Tab(1, 12)], 1);

        map.RecordFailure(11, ComFailureClass.Gone).ShouldBe(FailureEffect.Dropped);

        map.Contains(11).ShouldBeFalse();
        map.Contains(12).ShouldBeTrue();
        t.Handle.Disposals.ShouldBe(1);
    }

    [Fact]
    public void REQ031_Transient_failures_never_drop_an_entry()
    {
        using var map = new TabMap<FakeHandle>();
        map.Reconcile([Tab(1, 11)], 1);

        for (int i = 0; i < 50; i++)
        {
            map.RecordFailure(11, ComFailureClass.Transient).ShouldBe(FailureEffect.Kept);
        }

        map.Contains(11).ShouldBeTrue();
    }

    [Fact]
    public void REQ031_Other_failures_drop_on_the_third_in_a_row_and_a_success_resets_the_count()
    {
        using var map = new TabMap<FakeHandle>();
        var t = Tab(1, 11);
        map.Reconcile([t], 1);

        map.RecordFailure(11, ComFailureClass.Other).ShouldBe(FailureEffect.Kept);
        map.RecordFailure(11, ComFailureClass.Other).ShouldBe(FailureEffect.Kept);
        map.RecordSuccess(11, @"C:\x");
        map.RecordFailure(11, ComFailureClass.Other).ShouldBe(FailureEffect.Kept);   // count restarted
        map.RecordFailure(11, ComFailureClass.Other).ShouldBe(FailureEffect.Kept);
        map.RecordFailure(11, ComFailureClass.Other).ShouldBe(FailureEffect.Dropped);

        t.Handle.Disposals.ShouldBe(1);
        map.RecordFailure(11, ComFailureClass.Other).ShouldBe(FailureEffect.Unknown);
    }

    [Fact]
    public void REQ034_A_fresh_handle_from_a_rebuild_resets_the_failure_count()
    {
        using var map = new TabMap<FakeHandle>();
        map.Reconcile([Tab(1, 11)], 1);
        map.RecordFailure(11, ComFailureClass.Other);
        map.RecordFailure(11, ComFailureClass.Other);

        map.Reconcile([Tab(1, 11)], 1);

        map.RecordFailure(11, ComFailureClass.Other).ShouldBe(FailureEffect.Kept);
    }

    [Fact]
    public void NFR01_Capacity_evicts_the_least_recently_used_outside_the_foreground_frame()
    {
        using var map = new TabMap<FakeHandle>(capacity: 3);
        map.Reconcile([Tab(1, 11), Tab(2, 21), Tab(3, 31)], foregroundFrame: 1);
        map.TryGet(21, out _);   // 21 is now more recent than 31; 11 is the oldest but is foreground

        var r = map.Reconcile([Tab(1, 11), Tab(2, 21), Tab(3, 31), Tab(4, 41)], foregroundFrame: 1);

        r.Evicted.ShouldBe(1);
        map.Contains(31).ShouldBeFalse();
        map.Contains(11).ShouldBeTrue();   // foreground frame: protected even though least recently used
        map.Contains(21).ShouldBeTrue();
        map.Contains(41).ShouldBeTrue();
    }

    [Fact]
    public void NFR01_Capacity_never_evicts_the_foreground_frame_even_over_the_limit()
    {
        using var map = new TabMap<FakeHandle>(capacity: 2);

        var r = map.Reconcile([Tab(1, 11), Tab(1, 12), Tab(1, 13)], foregroundFrame: 1);

        r.Evicted.ShouldBe(0);
        map.Count.ShouldBe(3);
    }

    [Fact]
    public void NFR01_Churn_of_1000_rebuilds_with_changing_windows_leaves_no_live_handle()
    {
        var counter = new LiveCounter();
        using (var map = new TabMap<FakeHandle>(capacity: 8))
        {
            var rng = new Random(7);
            for (int i = 0; i < 1000; i++)
            {
                var list = new List<DiscoveredTab<FakeHandle>>();
                foreach (var t in Enumerable.Range(1, rng.Next(0, 12)).OrderBy(_ => rng.Next()))
                {
                    list.Add(new DiscoveredTab<FakeHandle>(t % 3 + 1, t, new FakeHandle(counter)));
                }

                map.Reconcile(list, foregroundFrame: rng.Next(1, 4));
                if (rng.Next(4) == 0)
                {
                    map.RemoveFrame(rng.Next(1, 4));
                }

                if (rng.Next(7) == 0)
                {
                    map.SweepDead(h => h % 2 == 0);
                }
            }
        }

        counter.Live.ShouldBe(0);   // Dispose released everything, each exactly once
    }

    [Fact]
    public void NFR01_Clear_and_Dispose_release_each_handle_exactly_once()
    {
        var a = Tab(1, 11); var b = Tab(1, 12);
        var map = new TabMap<FakeHandle>();
        map.Reconcile([a, b], 1);

        map.Clear();
        map.Dispose();

        a.Handle.Disposals.ShouldBe(1);
        b.Handle.Disposals.ShouldBe(1);
        map.Count.ShouldBe(0);
    }

    [Fact]
    public void REQ034_Resolver_picks_the_first_visible_tab()
    {
        var r = ActiveTabResolver.Resolve(100, [new ChildTab(11, false), new ChildTab(12, true), new ChildTab(13, true)]);
        r.ShouldBe(new ActiveTab(ActiveTabKind.Tab, 12));
    }

    [Fact]
    public void REQ034_Resolver_with_no_tab_windows_uses_the_frame_as_its_single_tab()
    {
        ActiveTabResolver.Resolve(100, []).ShouldBe(new ActiveTab(ActiveTabKind.ImplicitFrame, 100));
    }

    [Fact]
    public void REQ034_Resolver_with_tabs_but_none_visible_is_unresolved()
    {
        ActiveTabResolver.Resolve(100, [new ChildTab(11, false), new ChildTab(12, false)])
            .ShouldBe(new ActiveTab(ActiveTabKind.Unresolved, 0));
    }

    [Fact]
    public void REQ033_Foreground_is_the_frame_itself()
    {
        ForegroundResolver.Resolve(5, h => h == 5, _ => 0, _ => false).ShouldBe((nint)5);
    }

    [Fact]
    public void REQ038_Foreground_owned_by_a_frame_counts_as_that_frame_the_docked_panel_keeps_the_folder()
    {
        // panel (9) is owned by frame (5)
        ForegroundResolver.Resolve(9, h => h == 5, h => h == 9 ? 5 : 0, _ => false).ShouldBe((nint)5);
    }

    [Fact]
    public void REQ033_Foreground_that_is_not_explorer_or_owned_by_it_is_inactive()
    {
        ForegroundResolver.Resolve(9, h => h == 5, h => h == 9 ? 7 : 0, _ => false).ShouldBe((nint)0);
        ForegroundResolver.Resolve(0, _ => true, _ => 0, _ => false).ShouldBe((nint)0);
    }

    [Fact]
    public void REQ033_A_minimized_frame_is_inactive()
    {
        ForegroundResolver.Resolve(5, h => h == 5, _ => 0, h => h == 5).ShouldBe((nint)0);
    }

    [Fact]
    public void NFR01_An_owner_cycle_terminates()
    {
        ForegroundResolver.Resolve(1, _ => false, h => h == 1 ? 2 : 1, _ => false).ShouldBe((nint)0);
    }
}
