using System.Diagnostics;

/// P5: idle CPU of a global NAMECHANGE hook vs one scoped to Explorer's process, while another process
/// renames windows (a stand-in for browsers/terminals). The tracker's message loop only counts and filters.
static class P5
{
    public static int Churn(int everyMs)
    {
        var w = Native.CreateWindowExW(0x80, "STATIC", "x", 0x80000000, -32000, -32000, 10, 10, 0, 0, 0, 0);
        for (int i = 0; ; i++) { Native.NotifyWinEvent(Native.EVENT_OBJECT_NAMECHANGE, w, 0, 0); Thread.Sleep(everyMs); }
    }

    public static int Run()
    {
        Native.GetWindowThreadProcessId(Native.GetShellWindow(), out var shellPid);
        Console.WriteLine($"shell (explorer) pid {shellPid}");
        foreach (var every in new[] { 50, 5 })
        {
            var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"churn {every}") { UseShellExecute = false, CreateNoWindow = true })!;
            Thread.Sleep(1500);
            Console.WriteLine($"-- another process fires NAMECHANGE every {every} ms (~{1000 / every}/s) --");
            foreach (var (name, pid) in new[] { ("no hook", (uint)0), ("global hook", 0u), ("hook scoped to explorer pid", shellPid) })
            {
                bool install = name != "no hook";
                var (cpu, calls) = Harness.OnSta(() => Measure(install, pid, 10_000));
                Console.WriteLine($"   {name,-30} callbacks {calls,6}  our CPU {cpu:F3} % of one core over 10 s");
            }
            child.Kill(); child.WaitForExit();
        }
        return 0;
    }

    static (double cpuPct, int calls) Measure(bool install, uint pid, int ms)
    {
        int calls = 0; nint hook = 0;
        Native.WinEventProc proc = (_, ev, hwnd, io, ic, t, tm) =>
        {
            if (io != 0 || ic != 0) return;
            var root = Native.GetAncestor(hwnd, 2);
            if (Native.ClassName(root) != "CabinetWClass") { calls++; return; }
            calls++;
        };
        if (install) hook = Native.SetWinEventHook(Native.EVENT_OBJECT_NAMECHANGE, Native.EVENT_OBJECT_NAMECHANGE, 0, proc, pid, 0, Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
        var me = Process.GetCurrentProcess(); me.Refresh(); var cpu0 = me.TotalProcessorTime; var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            // GetMessage-style wait: sleep on the queue (an idle tracker loop does exactly this)
            Native.MsgWaitForMultipleObjects(0, 0, 0, 100, 0x4FF /*QS_ALLINPUT*/);
            while (Native.PeekMessageW(out var m, 0, 0, 0, 1) != 0) { Native.TranslateMessage(m); Native.DispatchMessageW(m); }
        }
        me.Refresh(); double used = (me.TotalProcessorTime - cpu0).TotalMilliseconds;
        if (hook != 0) Native.UnhookWinEvent(hook);
        GC.KeepAlive(proc);
        return (used / sw.Elapsed.TotalMilliseconds * 100, calls);
    }
}
