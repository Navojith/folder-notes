using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

/// P6 (T3.2.4 pre-work): how long does CoCancelCall ITSELF take, and can it block the thread that calls it?
/// The watchdog thread of the tracker calls it; if it could block, a blocked cancel would stop the abandon step.
/// Scratch server from P2; the "frozen server" cases suspend that scratch process (never Explorer).
static partial class P6
{
    static readonly Guid Clsid = new("5A8E7C10-3C0B-4F55-9D1E-A5C0DE5F0001");
    static string KeyPath => $@"Software\Classes\CLSID\{{{Clsid}}}\LocalServer32";

    [LibraryImport("ntdll.dll")] private static partial int NtSuspendProcess(nint h);
    [LibraryImport("ntdll.dll")] private static partial int NtResumeProcess(nint h);

    public static int Run()
    {
        using (var k = Registry.CurrentUser.CreateSubKey(KeyPath)) k.SetValue("", $"\"{Environment.ProcessPath}\" p2server");
        try { Harness.OnSta(() => { Scenarios(); return 0; }); }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\CLSID\{{{Clsid}}}", false);
            foreach (var p in Process.GetProcessesByName("S5Probes")) if (p.Id != Environment.ProcessId) { try { p.Kill(); } catch { } }
            Console.WriteLine("(registry key removed, server processes stopped)");
        }
        return 0;
    }

    /// Issue CoCancelCall from a fresh thread (optionally COM-initialised as MTA) and time only that call.
    static (int hr, double ms) Cancel(uint tid, uint timeoutSec, bool mta = false)
    {
        int hr = 0; double ms = 0;
        var t = new Thread(() =>
        {
            if (mta) Native.CoInitializeEx(0, Native.COINIT_MULTITHREADED);
            var sw = Stopwatch.StartNew(); hr = Native.CoCancelCall(tid, timeoutSec); ms = sw.Elapsed.TotalMilliseconds;
            if (mta) Native.CoUninitialize();
        });
        t.Start();
        if (!t.Join(30_000)) { Console.WriteLine("   !! CoCancelCall did not return within 30 s"); return (unchecked((int)0xDEAD), 30_000); }
        return (hr, ms);
    }

    static string H(int hr) => $"0x{hr:X8}";

    static void Scenarios()
    {
        int hr = Native.CoCreateInstance(Clsid, 0, Native.CLSCTX_LOCAL_SERVER, Ids.IPersistIid, out var p);
        Console.WriteLine($"CoCreateInstance(scratch local server) -> {H(hr)}");
        if (hr != 0) return;
        var obj = Com.Wrap<IPersist>(p); Marshal.Release(p);
        uint tid = Native.GetCurrentThreadId();
        Native.CoEnableCallCancellation(0);
        var server = Process.GetProcessesByName("S5Probes").First(x => x.Id != Environment.ProcessId);

        // 1. nothing in flight
        var r = Cancel(tid, 0); Console.WriteLine($"1  idle target thread (cancellation enabled)      : {H(r.hr)} in {r.ms:F2} ms");
        r = Cancel(tid, 0, mta: true); Console.WriteLine($"1b idle target, caller COM-initialised (MTA)       : {H(r.hr)} in {r.ms:F2} ms");
        // 2. bogus / dead thread ids
        r = Cancel(0x00FFFFFF, 0); Console.WriteLine($"2  nonexistent thread id                          : {H(r.hr)} in {r.ms:F2} ms");
        uint deadTid = 0; var dt = new Thread(() => deadTid = Native.GetCurrentThreadId()); dt.Start(); dt.Join();
        r = Cancel(deadTid, 0); Console.WriteLine($"2b thread that has already exited                  : {H(r.hr)} in {r.ms:F2} ms");
        // 3. target in a non-COM wait
        var stop = new ManualResetEventSlim(); uint waitTid = 0; var ready = new ManualResetEventSlim();
        var wt = new Thread(() => { Native.CoInitializeEx(0, Native.COINIT_APARTMENTTHREADED); Native.CoEnableCallCancellation(0); waitTid = Native.GetCurrentThreadId(); ready.Set(); stop.Wait(); }); wt.SetApartmentState(ApartmentState.STA); wt.Start(); ready.Wait();
        r = Cancel(waitTid, 0); Console.WriteLine($"3  target blocked in a NON-COM wait               : {H(r.hr)} in {r.ms:F2} ms");
        stop.Set(); wt.Join();

        // 4. hung (not frozen) server: baseline
        var call = TimedCall(obj, tid, 200, 0);
        Console.WriteLine($"4  hung server, cancel timeout 0                  : CoCancelCall {H(call.cancelHr)} in {call.cancelMs:F2} ms; call returned {H(call.callHr)} {call.afterCancelMs:F1} ms after the request");

        // 5. frozen server process (every thread suspended: no RPC thread can answer), cancel timeout 0
        NtSuspendProcess(server.Handle);
        try
        {
            call = TimedCall(obj, tid, 250, 0);
            Console.WriteLine($"5  FROZEN server process, cancel timeout 0        : CoCancelCall {H(call.cancelHr)} in {call.cancelMs:F2} ms; call returned {H(call.callHr)} {call.afterCancelMs:F1} ms after the request");
            // 6. frozen server, cancel timeout 3 s: shows what the documented wait does
            call = TimedCall(obj, tid, 250, 3);
            Console.WriteLine($"6  FROZEN server process, cancel timeout 3 s      : CoCancelCall {H(call.cancelHr)} in {call.cancelMs:F0} ms; call returned {H(call.callHr)} {call.afterCancelMs:F1} ms after the request");
            // 6b. no cancel at all against the frozen server: does the call ever return on its own within 4 s?
            var sw = Stopwatch.StartNew(); bool ret = false; var blocked = new Thread(() => { /* observe from outside */ });
            Console.WriteLine("6b (not run: without a cancel the call would block until the server resumes, by design)");
        }
        finally { NtResumeProcess(server.Handle); }

        // 7. stress: 150 cycles, cancel at a random 0-30 ms into the call; max cancel latency
        var rnd = new Random(7); double max = 0, sum = 0; int n = 0; var bad = new List<string>();
        for (int i = 0; i < 150; i++)
        {
            var c = TimedCall(obj, tid, rnd.Next(0, 30), 0);
            max = Math.Max(max, c.cancelMs); sum += c.cancelMs; n++;
            if (c.cancelMs > 50) bad.Add($"#{i}:{c.cancelMs:F0}ms");
        }
        Console.WriteLine($"7  150 random-time cancels (hung server)          : mean {sum / n:F2} ms, max {max:F2} ms; over 50 ms: {(bad.Count == 0 ? "none" : string.Join(",", bad))}");

        // 8. 3 threads cancel the same target at once (the real watchdog is one thread, but a retry must not deadlock)
        var results = new double[3]; var barrier = new Barrier(4);
        var cancelThreads = Enumerable.Range(0, 3).Select(i => new Thread(() => { barrier.SignalAndWait(); Thread.Sleep(100); var sw = Stopwatch.StartNew(); Native.CoCancelCall(tid, 0); results[i] = sw.Elapsed.TotalMilliseconds; })).ToList();
        cancelThreads.ForEach(t => t.Start());
        var w = Stopwatch.StartNew(); barrier.SignalAndWait(); int chr = obj.GetClassID(out _); double cms = w.Elapsed.TotalMilliseconds;
        cancelThreads.ForEach(t => t.Join());
        Console.WriteLine($"8  three concurrent cancels                        : call {H(chr)} after {cms:F0} ms; each CoCancelCall took {string.Join(", ", results.Select(x => x.ToString("F2")))} ms");
        Harness.Release(obj);
    }

    static (int callHr, double callMs, int cancelHr, double cancelMs, double afterCancelMs) TimedCall(IPersist obj, uint tid, int cancelAfterMs, uint timeoutSec)
    {
        int cancelHr = -1; double cancelMs = 0, cancelAt = 0; var sw = Stopwatch.StartNew();
        var wd = new Thread(() =>
        {
            Thread.Sleep(cancelAfterMs); cancelAt = sw.Elapsed.TotalMilliseconds;
            var s2 = Stopwatch.StartNew(); cancelHr = Native.CoCancelCall(tid, timeoutSec); cancelMs = s2.Elapsed.TotalMilliseconds;
        }) { IsBackground = true };
        wd.Start();
        int hr = obj.GetClassID(out _);
        double total = sw.Elapsed.TotalMilliseconds;
        wd.Join(10_000);
        return (hr, total, cancelHr, cancelMs, total - cancelAt);
    }
}
