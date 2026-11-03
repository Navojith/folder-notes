using System.Collections.Concurrent;
using FolderNotes.App.Explorer;
using FolderNotes.App.Explorer.Native;

namespace FolderNotes.App.Tests.Explorer;

/// <summary>04 B.7 and J (H1-H11): the hook thread's lifecycle, with a fake WinEvent API and one real Win32 smoke test.</summary>
public class HookThreadTests
{
    private sealed class FakeApi : IWinEventApi
    {
        private int _next = 100;
        public readonly ConcurrentQueue<string> Calls = new();
        public readonly ConcurrentDictionary<nint, (uint Min, uint Max, uint Pid, WinEventCallback Proc)> Hooks = new();
        public readonly ConcurrentBag<int> ThreadIds = [];
        public int FailSetHookCalls;                          // the first N SetHook calls return 0
        public ManualResetEventSlim? BlockSetHook;
        public ManualResetEventSlim? BlockUnhook;
        public int Installs;
        public int Unhooks;

        public nint SetHook(uint eventMin, uint eventMax, uint processId, WinEventCallback callback)
        {
            ThreadIds.Add(Environment.CurrentManagedThreadId);
            Calls.Enqueue($"set {eventMin:X}-{eventMax:X} pid={processId}");
            BlockSetHook?.Wait();
            if (Interlocked.Decrement(ref FailSetHookCalls) >= 0)
            {
                return 0;
            }

            Interlocked.Increment(ref Installs);
            nint h = Interlocked.Increment(ref _next);
            Hooks[h] = (eventMin, eventMax, processId, callback);
            return h;
        }

        public bool Unhook(nint hook)
        {
            ThreadIds.Add(Environment.CurrentManagedThreadId);
            Calls.Enqueue("unhook");
            BlockUnhook?.Wait();
            Interlocked.Increment(ref Unhooks);
            return Hooks.TryRemove(hook, out _);
        }

        /// <summary>Deliver an event to every hook whose range covers it (as the system would).</summary>
        public void Fire(uint ev, int idObject = 0)
        {
            foreach (var h in Hooks.Values.Where(h => ev >= h.Min && ev <= h.Max).ToArray())
            {
                h.Proc(0, ev, 1, idObject, 0, 0, 0);
            }
        }
    }

    private sealed class Target : IWakeTarget
    {
        public int Posts;

        public bool TryPost()
        {
            Interlocked.Increment(ref Posts);
            return true;
        }
    }

    private static readonly HookThreadOptions Quick = new() { StartTimeout = TimeSpan.FromMilliseconds(300), StopTimeout = TimeSpan.FromMilliseconds(300) };

    private readonly FakeApi _api = new();
    private readonly SignalRouter _router = new();
    private readonly TrackerProcessGuard _guard = new();

    private HookThread Host(HookThreadOptions? o = null, TrackerProcessGuard? guard = null) =>
        new(_api, _router, guard ?? _guard, null, o ?? Quick);

    private static void WaitFor(Func<bool> condition, string what) =>
        SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(5)).ShouldBeTrue($"timed out waiting for: {what}");

    // ---- lifecycle -----------------------------------------------------------------------------------

    [Fact]
    public void NFR01_Start_installs_the_always_on_hook_on_the_hook_thread_and_takes_one_budget_slot()
    {
        using var h = Host();

        h.Start().ShouldBe(HookStartResult.Started);

        h.HooksActive.ShouldBeTrue();
        h.InstalledHookCount.ShouldBe(1);
        _api.Calls.ShouldBe(["set 3-3 pid=0"]);               // EVENT_SYSTEM_FOREGROUND, every process
        _api.ThreadIds.Distinct().Single().ShouldBe(h.ManagedThreadId);
        h.ManagedThreadId.ShouldNotBe(Environment.CurrentManagedThreadId);
        _guard.LiveThreads(TrackerThreadKind.Hook).ShouldBe(1);
    }

    [Fact]
    public void NFR01_Install_re_scope_and_uninstall_all_happen_on_the_one_hook_thread()
    {
        using var h = Host();
        h.Start();

        h.SetScope(1234);
        WaitFor(() => h.InstalledHookCount == 1 + WinEventMap.ScopedRanges.Length, "scoped install");
        h.SetScope(5678);
        WaitFor(() => h.ScopePid == 5678 && h.InstalledHookCount == 5, "re-scope");
        h.Stop().ShouldBeTrue();

        _api.ThreadIds.Distinct().Count().ShouldBe(1);        // H7: nothing ever touched a hook from another thread
        _api.ThreadIds.Distinct().Single().ShouldNotBe(Environment.CurrentManagedThreadId);
    }

    [Fact]
    public void NFR01_Scoped_hooks_listen_to_the_requested_process_only_and_SetScope_zero_removes_them()
    {
        using var h = Host();
        h.Start();

        h.SetScope(4321);
        WaitFor(() => h.ScopePid == 4321 && h.InstalledHookCount == 5, "scope 4321");
        _api.Hooks.Values.Count(x => x.Pid == 4321).ShouldBe(4);
        _api.Hooks.Values.Count(x => x.Pid == 0).ShouldBe(1);   // only the foreground hook is global

        h.SetScope(0);
        WaitFor(() => h.ScopePid == 0 && h.InstalledHookCount == 1, "scope removed");
        _api.Hooks.Values.ShouldAllBe(x => x.Pid == 0);
    }

    [Fact]
    public void NFR01_A_stale_scope_request_never_overrides_a_newer_one()
    {
        using var h = Host();
        h.Start();

        h.SetScopeWithSequence(111, sequence: 10);
        WaitFor(() => h.ScopePid == 111, "newer scope");
        h.SetScopeWithSequence(222, sequence: 9);             // arrives late (H8)
        h.SetScopeWithSequence(333, sequence: 11);
        WaitFor(() => h.ScopePid == 333, "newest scope");

        _api.Hooks.Values.Where(x => x.Pid != 0).ShouldAllBe(x => x.Pid == 333);
        _api.Calls.ShouldNotContain(c => c.EndsWith("pid=222"));
    }

    [Fact]
    public void NFR01_Stop_removes_every_hook_before_the_thread_ends_and_frees_the_slot()
    {
        var h = Host();
        h.Start();
        h.SetScope(99);
        WaitFor(() => h.InstalledHookCount == 5, "scoped install");

        h.Stop().ShouldBeTrue();

        _api.Hooks.ShouldBeEmpty();
        _api.Unhooks.ShouldBe(5);
        h.HooksActive.ShouldBeFalse();
        h.Abandoned.ShouldBeFalse();
        _guard.LiveThreads(TrackerThreadKind.Hook).ShouldBe(0);
        _guard.AbandonedHookThreads.ShouldBe(0);
    }

    [Fact]
    public void NFR01_Stop_before_Start_Stop_twice_and_Dispose_are_harmless()
    {
        var h = Host();
        h.Stop().ShouldBeTrue();                              // never started
        h.Start().ShouldBe(HookStartResult.Started);
        h.Stop().ShouldBeTrue();
        h.Stop().ShouldBeTrue();
        h.Dispose();
        _guard.OwnedThreads.ShouldBe(0);
    }

    [Fact]
    public void NFR01_SetScope_when_not_running_does_nothing()
    {
        using var h = Host();
        h.SetScope(7).ShouldBeFalse();
        h.Start();
        h.Stop();
        h.SetScope(7).ShouldBeFalse();
        _api.Hooks.ShouldBeEmpty();
    }

    [Fact]
    public void NFR01_Start_after_Stop_creates_one_new_thread_and_never_two_live()
    {
        using var h = Host();
        h.Start().ShouldBe(HookStartResult.Started);
        h.Start().ShouldBe(HookStartResult.AlreadyStarted);
        h.ThreadsCreated.ShouldBe(1);

        h.Stop();
        h.Start().ShouldBe(HookStartResult.Started);

        h.ThreadsCreated.ShouldBe(2);                         // one creation per Start (bounded)
        _guard.LiveThreads(TrackerThreadKind.Hook).ShouldBe(1);
    }

    [Fact]
    public void NFR06_The_hook_thread_budget_is_process_wide_two_hosts_cannot_both_run()
    {
        using var a = Host();
        using var b = Host();

        a.Start().ShouldBe(HookStartResult.Started);
        b.Start().ShouldBe(HookStartResult.BudgetRefused);
        b.ThreadsCreated.ShouldBe(0);
        b.HooksActive.ShouldBeFalse();

        a.Stop();
        b.Start().ShouldBe(HookStartResult.Started);
    }

    // ---- failure modes -------------------------------------------------------------------------------

    [Fact]
    public void NFR01_H1_When_the_hook_cannot_be_installed_the_tracker_is_poll_only_and_the_slot_is_released()
    {
        _api.FailSetHookCalls = 1;
        using var h = Host();

        h.Start().ShouldBe(HookStartResult.InstallFailed);

        h.HooksActive.ShouldBeFalse();
        WaitFor(() => _guard.LiveThreads(TrackerThreadKind.Hook) == 0, "slot release");
        h.Abandoned.ShouldBeFalse();                          // a clean failure is not an abandonment: a later Start may retry
        h.Start().ShouldBe(HookStartResult.Started);
    }

    [Fact]
    public void NFR01_H2_A_hook_thread_that_will_not_unhook_is_abandoned_after_the_bounded_wait_and_never_replaced()
    {
        using var release = new ManualResetEventSlim(false);
        var h = Host();
        h.Start();
        _api.BlockUnhook = release;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        bool clean = h.Stop();

        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(3)); // bounded: 300 ms configured
        clean.ShouldBeFalse();
        h.Abandoned.ShouldBeTrue();
        h.HooksActive.ShouldBeFalse();
        _guard.AbandonedHookThreads.ShouldBe(1);
        _guard.LiveThreads(TrackerThreadKind.Hook).ShouldBe(0);
        h.Start().ShouldBe(HookStartResult.BudgetRefused);    // never replaced in this process
        Host().Start().ShouldBe(HookStartResult.BudgetRefused);   // not even through another object
        h.SetScope(5).ShouldBeFalse();

        release.Set();                                        // let the zombie finish so the test leaves nothing behind
    }

    [Fact]
    public void NFR01_H2_A_hook_thread_that_never_answers_Start_is_abandoned()
    {
        using var release = new ManualResetEventSlim(false);
        _api.BlockSetHook = release;
        var h = Host();

        h.Start().ShouldBe(HookStartResult.TimedOut);

        h.Abandoned.ShouldBeTrue();
        h.HooksActive.ShouldBeFalse();
        _guard.AbandonedHookThreads.ShouldBe(1);
        release.Set();
    }

    [Fact]
    public void NFR01_H7_The_zombie_that_wakes_up_later_does_not_free_the_slot_twice()
    {
        using var release = new ManualResetEventSlim(false);
        var h = Host();
        h.Start();
        _api.BlockUnhook = release;
        h.Stop().ShouldBeFalse();

        release.Set();                                        // the zombie now ends and runs its cleanup
        Thread.Sleep(200);

        _guard.AbandonedHookThreads.ShouldBe(1);
        _guard.LiveThreads(TrackerThreadKind.Hook).ShouldBe(0);
        _guard.OwnedThreads.ShouldBe(1);                      // still counted as ours, exactly once
    }

    // ---- callbacks ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(WinEventMap.SystemForeground, (int)TrackerSignal.Foreground)]
    [InlineData(WinEventMap.ObjectNameChange, (int)TrackerSignal.NameChange)]
    [InlineData(WinEventMap.ObjectShow, (int)TrackerSignal.ShowHide)]
    [InlineData(WinEventMap.ObjectHide, (int)TrackerSignal.ShowHide)]
    [InlineData(WinEventMap.SystemMinimizeStart, (int)TrackerSignal.Minimize)]
    [InlineData(WinEventMap.SystemMinimizeEnd, (int)TrackerSignal.Minimize)]
    [InlineData(WinEventMap.ObjectCreate, (int)TrackerSignal.CreateDestroy)]
    [InlineData(WinEventMap.ObjectDestroy, (int)TrackerSignal.CreateDestroy)]
    [InlineData(0x9999u, 0)]
    public void NFR01_Each_event_maps_to_exactly_one_signal(uint ev, int expected) =>
        WinEventMap.ToSignal(ev).ShouldBe((TrackerSignal)expected);

    [Fact]
    public void NFR01_The_callback_raises_the_events_bit_and_ignores_non_window_objects()
    {
        using var h = Host();
        h.Start();
        h.SetScope(77);
        WaitFor(() => h.InstalledHookCount == 5, "scope");

        _api.Fire(WinEventMap.ObjectNameChange, idObject: -4);   // OBJID_CARET etc.: not a window title
        _router.Pending.ShouldBe(TrackerSignal.None);

        _api.Fire(WinEventMap.ObjectNameChange);
        _router.Pending.ShouldBe(TrackerSignal.NameChange);
    }

    [Fact]
    public void NFR06_H3_An_event_storm_costs_one_wake_up_and_no_work_beyond_the_bit()
    {
        using var h = Host();
        h.Start();
        h.SetScope(77);
        WaitFor(() => h.InstalledHookCount == 5, "scope");
        var worker = new Target();
        _router.Attach(worker);

        for (int i = 0; i < 20_000; i++)
        {
            _api.Fire(WinEventMap.ObjectNameChange);
        }

        worker.Posts.ShouldBe(1);
        _router.Consume(worker).ShouldBe(TrackerSignal.NameChange);
    }

    [Fact]
    public void NFR01_Events_while_no_worker_is_attached_are_kept_for_the_next_one()
    {
        using var h = Host();
        h.Start();
        _api.Fire(WinEventMap.SystemForeground);

        var worker = new Target();
        _router.Attach(worker);

        worker.Posts.ShouldBe(1);
        _router.Consume(worker).ShouldBe(TrackerSignal.Foreground);
    }

    // ---- worker replacement ------------------------------------------------------------------------

    [Fact]
    public void NFR01_Replacing_an_abandoned_worker_does_not_touch_the_hooks()
    {
        using var h = Host();
        h.Start();
        h.SetScope(55);
        WaitFor(() => h.InstalledHookCount == 5, "scope");
        var oldWorker = new Target();
        _router.Attach(oldWorker);
        var callsBefore = _api.Calls.Count;
        int hookThread = h.ManagedThreadId;

        // the supervisor abandons the wedged worker and starts a replacement
        _router.Detach(oldWorker);
        var newWorker = new Target();
        _router.Attach(newWorker);
        _api.Fire(WinEventMap.ObjectShow);

        _api.Calls.Count.ShouldBe(callsBefore);               // no install, no uninstall
        h.InstalledHookCount.ShouldBe(5);
        h.ManagedThreadId.ShouldBe(hookThread);               // same thread, same hooks
        h.ThreadsCreated.ShouldBe(1);
        newWorker.Posts.ShouldBe(1);
        oldWorker.Posts.ShouldBe(0);
        _router.Consume(oldWorker).ShouldBe(TrackerSignal.None);
        _router.Consume(newWorker).ShouldBe(TrackerSignal.ShowHide);
    }

    [Fact]
    public void NFR01_Events_in_the_gap_between_the_old_and_the_replacement_worker_reach_the_replacement()
    {
        using var h = Host();
        h.Start();
        h.SetScope(55);
        WaitFor(() => h.InstalledHookCount == 5, "scope");
        var oldWorker = new Target();
        _router.Attach(oldWorker);
        _router.Detach(oldWorker);

        _api.Fire(WinEventMap.ObjectNameChange);              // nobody is attached
        _api.Fire(WinEventMap.ObjectCreate);
        var fresh = new Target();
        _router.Attach(fresh);

        fresh.Posts.ShouldBe(1);
        _router.Consume(fresh).ShouldBe(TrackerSignal.NameChange | TrackerSignal.CreateDestroy);
    }

    // ---- real Win32 (no Explorer involved) ---------------------------------------------------------

    [Fact]
    public void NFR01_Real_hook_thread_receives_an_event_through_its_message_loop_and_ends_cleanly()
    {
        var router = new SignalRouter();
        var guard = new TrackerProcessGuard();
        var h = new HookThread(new Win32WinEventApi(skipOwnProcess: false), router, guard, null, HookThreadOptions.Default);
        var worker = new Target();
        router.Attach(worker);

        h.Start().ShouldBe(HookStartResult.Started);
        h.SetScope((uint)Environment.ProcessId);
        WaitFor(() => h.InstalledHookCount == 1 + WinEventMap.ScopedRanges.Length, "real scoped install");

        // A message-only window of this process (scoping is by the window's process; the desktop window belongs to another).
        nint window = CreateWindowExW(0, "STATIC", "probe", 0, 0, 0, 0, 0, -3, 0, 0, 0);
        window.ShouldNotBe((nint)0);
        bool delivered;
        try
        {
            delivered = SpinWait.SpinUntil(() =>
            {
                MessageLoopNative.NotifyWinEvent(WinEventMap.ObjectNameChange, window, 0, 0);
                Thread.Sleep(20);
                return router.Pending.HasFlag(TrackerSignal.NameChange);
            }, TimeSpan.FromSeconds(5));
        }
        finally
        {
            DestroyWindow(window);
        }

        h.Stop().ShouldBeTrue();
        delivered.ShouldBeTrue("the real out-of-context hook never delivered the event to the hook thread");
        worker.Posts.ShouldBeGreaterThan(0);
        h.InstalledHookCount.ShouldBe(0);
        guard.OwnedThreads.ShouldBe(0);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern nint CreateWindowExW(uint ex, string cls, string name, uint style, int x, int y, int w, int h, nint parent, nint menu, nint inst, nint param);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint hwnd);
}
