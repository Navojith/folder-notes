using FolderNotes.App.Explorer;
using FolderNotes.App.Explorer.Com;
using FolderNotes.App.Explorer.Native;

namespace FolderNotes.App.Tests.Explorer;

/// <summary>A scriptable gateway for fault injection (04 A). Counts every handle it hands out and disposes.</summary>
internal sealed class FakeShellWindowsGateway : IShellWindowsGateway
{
    internal sealed class FakeHandle(FakeShellWindowsGateway owner, nint tab) : TabHandle
    {
        public nint Tab { get; } = tab;

        public bool Disposed { get; private set; }

        public override void Dispose()
        {
            if (!Disposed)
            {
                Disposed = true;
                Interlocked.Decrement(ref owner._live);
            }
        }
    }

    private int _live;

    public int Live => Volatile.Read(ref _live);

    public List<(nint Frame, nint Tab)> Tabs { get; } = [];

    public Dictionary<nint, PathReadResult> Paths { get; } = [];

    public int EnumerateHr { get; set; }

    public int Enumerations { get; private set; }

    public bool Disposed { get; private set; }

    public int TryEnumerateTabs(List<DiscoveredTab<TabHandle>> into)
    {
        Enumerations++;
        if (EnumerateHr < 0)
        {
            return EnumerateHr;
        }

        foreach (var (frame, tab) in Tabs)
        {
            Interlocked.Increment(ref _live);
            into.Add(new DiscoveredTab<TabHandle>(frame, tab, new FakeHandle(this, tab)));
        }

        return 0;
    }

    public PathReadResult TryReadPath(TabHandle tab) =>
        tab is FakeHandle h && Paths.TryGetValue(h.Tab, out var r) ? r : PathReadResult.Failure(ComNative.EFail);

    public void Dispose() => Disposed = true;
}

/// <summary>04 B.3 ComRef, the fake gateway with the real TabMap, and the real COM gateway on a real STA worker.</summary>
[Trait("Kind", "Timing")]
public class ComLayerTests
{
    // ---- ComRef -------------------------------------------------------------------------------------

    [Fact]
    public void NFR01_ComRef_counts_live_wrappers_and_Dispose_is_idempotent()
    {
        var t = new RcwTracker();
        var a = new ComRef<object>(new object(), t);
        var b = new ComRef<object>(new object(), t);
        t.Live.ShouldBe(2);

        a.Dispose();
        a.Dispose();                                          // second dispose changes nothing

        t.Live.ShouldBe(1);
        a.IsReleased.ShouldBeTrue();
        Should.Throw<ObjectDisposedException>(() => _ = a.Value);
        b.Value.ShouldNotBeNull();
        b.Dispose();
        t.Live.ShouldBe(0);
        t.Created.ShouldBe(2);
        t.CrossThreadReleases.ShouldBe(0);
    }

    [Fact]
    public void NFR01_ComRef_flags_a_release_from_another_thread()
    {
        var t = new RcwTracker();
        var r = new ComRef<object>(new object(), t);

        var other = new Thread(r.Dispose);                    // a dedicated thread: certainly not the creating one
        other.Start();
        other.Join();                                         // the rule 04 B.2 forbids this: made visible

        t.Live.ShouldBe(0);
        t.CrossThreadReleases.ShouldBe(1);
    }

    // ---- fake gateway through the real TabMap ------------------------------------------------------

    [Fact]
    public void NFR01_Handles_handed_to_the_TabMap_are_all_disposed_by_Clear()
    {
        var gw = new FakeShellWindowsGateway();
        gw.Tabs.AddRange([(1, 11), (1, 12), (2, 21)]);
        using var map = new TabMap<TabHandle>();
        var found = new List<DiscoveredTab<TabHandle>>();

        gw.TryEnumerateTabs(found).ShouldBe(0);
        map.Reconcile(found, nint.Zero);
        gw.Live.ShouldBe(3);

        map.Clear();

        gw.Live.ShouldBe(0);                                  // "live-RCW count is 0 after every Rebuild that dropped entries"
    }

    [Fact]
    public void NFR01_A_root_failure_is_returned_as_an_HRESULT_for_the_classifier_not_thrown()
    {
        var gw = new FakeShellWindowsGateway { EnumerateHr = unchecked((int)0x80010108) };

        int hr = gw.TryEnumerateTabs([]);

        ComFailureClassifier.Classify(hr).ShouldBe(ComFailureClass.Gone);
    }

    // ---- the real COM gateway, on a real STA worker (read-only: it only reads IShellWindows) ---------

    private static (StaWorkerHost Host, TaskCompletionSource<bool> Done) RunOnWorker(Action<StaWorkerHost> body)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new StaWorkerHost(0, _ => { }, _ => { }, _ => { });
        host.Start(TimeSpan.FromSeconds(5)).ShouldBeTrue();
        host.Post(() =>
        {
            try
            {
                body(host);
                done.TrySetResult(true);
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }
        }).ShouldBeTrue();
        return (host, done);
    }

    [Fact]
    public async Task NFR01_Real_gateway_enumerates_reads_and_releases_everything_on_the_worker_thread()
    {
        var tracker = new RcwTracker();
        int createdHr = 0;
        int tabs = 0;
        int readsOk = 0;
        int callThread = 0;
        var (host, done) = RunOnWorker(h =>
        {
            callThread = Environment.CurrentManagedThreadId;
            createdHr = new ComShellWindowsGatewayFactory(tracker).TryCreate(out var gw);
            if (createdHr < 0 || gw is null)
            {
                return;
            }

            using (gw)
            {
                var found = new List<DiscoveredTab<TabHandle>>();
                gw.TryEnumerateTabs(found).ShouldBe(0);
                tabs = found.Count;
                foreach (var tab in found)
                {
                    var r = gw.TryReadPath(tab.Handle);
                    if (r.Hr >= 0)
                    {
                        readsOk++;
                    }

                    tab.Handle.Dispose();
                }
            }
        });

        await done.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        host.Stop(TimeSpan.FromSeconds(5)).ShouldBeTrue();

        if (createdHr < 0)
        {
            Assert.Skip($"IShellWindows is not available in this session (0x{createdHr:X8}).");
        }

        callThread.ShouldBe(host.ManagedThreadId);
        tracker.Live.ShouldBe(0);                             // 04 B.3: the live-RCW counter is 0 at the end
        tracker.CrossThreadReleases.ShouldBe(0);              // every release ran on the thread that acquired
        tracker.Created.ShouldBeGreaterThan(0);
        readsOk.ShouldBeLessThanOrEqualTo(tabs);
    }

    [Fact]
    public async Task NFR01_Real_gateway_never_throws_for_a_foreign_or_released_handle()
    {
        var tracker = new RcwTracker();
        PathReadResult foreign = default;
        PathReadResult released = default;
        int hr = 0;
        var (host, done) = RunOnWorker(_ =>
        {
            hr = new ComShellWindowsGatewayFactory(tracker).TryCreate(out var gw);
            if (hr < 0 || gw is null)
            {
                return;
            }

            using (gw)
            {
                foreign = gw.TryReadPath(new FakeShellWindowsGateway.FakeHandle(new FakeShellWindowsGateway(), 5));
                var found = new List<DiscoveredTab<TabHandle>>();
                gw.TryEnumerateTabs(found);
                foreach (var t in found)
                {
                    t.Handle.Dispose();
                    released = gw.TryReadPath(t.Handle);      // a stale cached handle must be a failure value, not a crash
                }
            }
        });

        await done.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        host.Stop(TimeSpan.FromSeconds(5)).ShouldBeTrue();
        if (hr < 0)
        {
            Assert.Skip("IShellWindows is not available in this session.");
        }

        foreign.Hr.ShouldBeLessThan(0);
        if (released != default)
        {
            released.Hr.ShouldBeLessThan(0);
        }

        tracker.Live.ShouldBe(0);
    }
}
