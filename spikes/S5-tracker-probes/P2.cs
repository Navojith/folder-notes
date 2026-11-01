using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Microsoft.Win32;

[GeneratedComInterface][Guid("00000001-0000-0000-C000-000000000046")]
partial interface IClassFactory
{
    [PreserveSig] int CreateInstance(nint outer, in Guid riid, out nint ppv);
    [PreserveSig] int LockServer([MarshalAs(UnmanagedType.Bool)] bool lockIt);
}

/// Server-side object: IPersist.GetWindow blocks (simulates a hung Explorer); ContextSensitiveHelp is a fast call.
[GeneratedComClass]
partial class SlowWindow : IPersist
{
    public static int SleepMs = 6000;
    public int GetClassID(out Guid clsid) { P2.Log("GetClassID enter"); Thread.Sleep(SleepMs); clsid = Guid.Empty; P2.Log("GetClassID exit"); return 0; }
}

[GeneratedComClass]
partial class SlowFactory : IClassFactory
{
    public int CreateInstance(nint outer, in Guid riid, out nint ppv)
    {
        P2.Log("CreateInstance " + riid);
        nint unk = (nint)Com.Wrappers.GetOrCreateComInterfaceForObject(new SlowWindow(), CreateComInterfaceFlags.None);
        try { return Marshal.QueryInterface(unk, in riid, out ppv); } finally { Marshal.Release(unk); }
    }
    public int LockServer(bool lockIt) => 0;
}

/// P2: can a watchdog thread cancel a blocked out-of-process COM call (CoCancelCall)? Also: do WinEvent callbacks
/// run on the blocked STA thread while it waits?
static class P2
{
    static readonly Guid Clsid = new("5A8E7C10-3C0B-4F55-9D1E-A5C0DE5F0001");
    static string KeyPath => $@"Software\Classes\CLSID\{{{Clsid}}}\LocalServer32";

    public static void Log(string s) { try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "fn-s5-server.log"), $"{DateTime.Now:HH:mm:ss.fff} {s}{Environment.NewLine}"); } catch { } }

    public static int Server()
    {
        var t = new Thread(ServerBody); t.SetApartmentState(ApartmentState.MTA); t.Start(); t.Join(); return 0;
    }

    static void ServerBody()
    {
        Log("server start");
        Native.CoInitializeEx(0, Native.COINIT_MULTITHREADED);
        nint f = (nint)Com.Wrappers.GetOrCreateComInterfaceForObject(new SlowFactory(), CreateComInterfaceFlags.None);
        int hr = Native.CoRegisterClassObject(Clsid, f, Native.CLSCTX_LOCAL_SERVER, 1 /*REGCLS_MULTIPLEUSE*/, out var cookie);
        Log("registered hr=" + hr);
        if (hr != 0) return;
        Thread.Sleep(90_000);   // the client kills us earlier; this is only a safety net
    }

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

    static void Scenarios()
    {
        Console.WriteLine("creating instance (launches the scratch server)...");
        int hr = Native.CoCreateInstance(Clsid, 0, Native.CLSCTX_LOCAL_SERVER, Ids.IPersistIid, out var p);
        Console.WriteLine($"CoCreateInstance(local server) -> {Com.Hr(hr)}");
        if (hr != 0) return;
        var obj = Com.Wrap<IPersist>(p); Marshal.Release(p);
        uint tid = Native.GetCurrentThreadId();

        // ---- A: cancellation enabled, watchdog cancels at 250 ms (no hooks, no message loop)
        Native.CoEnableCallCancellation(0);
        var a = BlockedCall(obj, tid, 250);
        Console.WriteLine($"A  cancel@250ms: call returned {Com.Hr(a.hr)} after {a.callMs:F0} ms; CoCancelCall -> {Com.Hr(a.cancelHr)} issued at {a.cancelAt:F0} ms; call returned {a.afterCancel:F1} ms after the cancel request");
        var lat = new List<double>(); var hrs = new HashSet<string>();
        for (int i = 0; i < 5; i++) { var r = BlockedCall(obj, tid, 150); lat.Add(r.afterCancel); hrs.Add(Com.Hr(r.hr)); }
        Console.WriteLine($"B  5 more cancel cycles: ms from cancel request to return: {string.Join(", ", lat.Select(x => x.ToString("F1")))}; hr {string.Join("/", hrs)}");

        // ---- C: cancellation NOT enabled on another thread: CoCancelCall must fail and the call run to the end
        double cMs = 0; int cHr = 0, cCancelHr = 0;
        var t2 = new Thread(() =>
        {
            Native.CoInitializeEx(0, Native.COINIT_APARTMENTTHREADED);
            Native.CoCreateInstance(Clsid, 0, Native.CLSCTX_LOCAL_SERVER, Ids.IPersistIid, out var q);
            var o2 = Com.Wrap<IPersist>(q); Marshal.Release(q);
            uint tid2 = Native.GetCurrentThreadId();
            var wd = new Thread(() => { Thread.Sleep(250); cCancelHr = Native.CoCancelCall(tid2, 0); }) { IsBackground = true }; wd.Start();
            var sw = Stopwatch.StartNew(); cHr = o2.GetClassID(out _); cMs = sw.Elapsed.TotalMilliseconds;
            Native.CoUninitialize();
        });
        t2.SetApartmentState(ApartmentState.STA); t2.Start(); t2.Join();
        Console.WriteLine($"C  cancellation NOT enabled: CoCancelCall -> {Com.Hr(cCancelHr)}, call returned {Com.Hr(cHr)} after {cMs:F0} ms (server sleeps {SlowWindow.SleepMs})");
        Harness.Release(obj);

        // ---- D: the tracker's real shape: message loop + WinEvent hook; the COM call runs inside a posted-message handler.
        //         Are hook callbacks delivered while the thread is blocked in the call? Can the call be cancelled from there?
        int hrD = 0; double dMs = 0, dAfter = 0; int dHookDuring = 0, dHookTotal = 0, dCancelHr = 0; bool dReentered = false;
        var t3 = new Thread(() =>
        {
            Native.CoInitializeEx(0, Native.COINIT_APARTMENTTHREADED);
            Native.CoEnableCallCancellation(0);
            Native.CoCreateInstance(Clsid, 0, Native.CLSCTX_LOCAL_SERVER, Ids.IPersistIid, out var q);
            var o3 = Com.Wrap<IPersist>(q); Marshal.Release(q);
            uint tid3 = Native.GetCurrentThreadId();
            bool inCall = false; int during = 0, total = 0;
            Native.WinEventProc proc = (_, ev, hw, io, ic, th, tm) => { total++; if (inCall) during++; };
            var hook = Native.SetWinEventHook(Native.EVENT_OBJECT_NAMECHANGE, Native.EVENT_OBJECT_NAMECHANGE, 0, proc, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
            var stop = new ManualResetEventSlim();
            var churn = new Thread(() =>
            {
                var w = Native.CreateWindowExW(0x80, "STATIC", "x", 0x90000000, -32000, -32000, 10, 10, 0, 0, 0, 0);
                for (int i = 0; !stop.IsSet; i++) { Native.SetWindowTextW(w, "t" + i); Native.NotifyWinEvent(Native.EVENT_OBJECT_NAMECHANGE, w, 0, 0); Thread.Sleep(20); }
                Native.DestroyWindow(w);
            }) { IsBackground = true };
            churn.Start();
            Native.PostThreadMessageW(tid3, 0x8001 /*WM_APP+1*/, 0, 0);
            int calls = 0;
            while (Native.GetMessageW(out var m, 0, 0, 0) > 0)
            {
                if (m.message == 0x8001)
                {
                    calls++;
                    if (calls > 1) { dReentered = true; continue; }
                    var sw = Stopwatch.StartNew(); double cancelAt = 0;
                    var wd = new Thread(() => { Thread.Sleep(250); cancelAt = sw.Elapsed.TotalMilliseconds; dCancelHr = Native.CoCancelCall(tid3, 0); }) { IsBackground = true };
                    inCall = true; wd.Start();
                    hrD = o3.GetClassID(out _); dMs = sw.Elapsed.TotalMilliseconds; inCall = false; dAfter = dMs - cancelAt;
                    dHookDuring = during;
                    Native.PostThreadMessageW(tid3, 0x8002, 0, 0);   // let queued events drain, then quit
                    continue;
                }
                if (m.message == 0x8002) { Native.PostThreadMessageW(tid3, 0x8003, 0, 0); continue; }
                if (m.message == 0x8003) { var pump = Stopwatch.StartNew(); while (pump.ElapsedMilliseconds < 400) { while (Native.PeekMessageW(out var mm, 0, 0, 0, 1) != 0) { Native.TranslateMessage(mm); Native.DispatchMessageW(mm); } Thread.Sleep(5); } dHookTotal = total; break; }
                Native.TranslateMessage(m); Native.DispatchMessageW(m);
            }
            stop.Set(); Native.UnhookWinEvent(hook); Harness.Release(o3);
            Native.CoUninitialize();
        });
        t3.SetApartmentState(ApartmentState.STA); t3.Start(); t3.Join();
        Console.WriteLine($"D  in a message loop with a WinEvent hook: call returned {Com.Hr(hrD)} after {dMs:F0} ms; CoCancelCall -> {Com.Hr(dCancelHr)}; returned {dAfter:F1} ms after the cancel request");
        Console.WriteLine($"   hook callbacks run on the blocked thread during the call: {dHookDuring}; delivered in total once it returned/pumped: {dHookTotal}; message handler re-entered: {dReentered}");
    }

    static (int hr, double callMs, int cancelHr, double cancelAt, double afterCancel) BlockedCall(IPersist obj, uint tid, int cancelAfterMs)
    {
        int cancelHr = -1; double cancelAt = 0; var sw = Stopwatch.StartNew();
        var wd = new Thread(() => { Thread.Sleep(cancelAfterMs); cancelAt = sw.Elapsed.TotalMilliseconds; cancelHr = Native.CoCancelCall(tid, 0); }) { IsBackground = true };
        wd.Start();
        int hr = obj.GetClassID(out _);
        double total = sw.Elapsed.TotalMilliseconds;
        return (hr, total, cancelHr, cancelAt, total - cancelAt);
    }
}
