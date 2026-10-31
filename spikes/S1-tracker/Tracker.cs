using System.Diagnostics;
using System.Runtime.InteropServices;

public sealed record FolderState(IntPtr Frame, string? Path, string Signal, long Ticks, double EvalMs, string TabMethod);

/// Spike tracker: runs its own STA thread + message loop. Hooks only *trigger* an evaluation
/// (posted message); all COM work happens in the loop, never inside a WinEvent callback.
public sealed class Tracker : IDisposable
{
    static readonly Guid SID_STopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    static readonly Guid IID_IShellBrowser = new("000214E2-0000-0000-C000-000000000046");

    public event Action<FolderState>? Changed;
    public readonly List<double> EvalCosts = new();
    public int TabMethodDisagreements;
    public bool EnablePoll = true;
    public bool EnableEvents = true;
    public uint PollMs = 250;

    readonly Thread _thread;
    readonly ManualResetEventSlim _ready = new();
    readonly List<Native.WinEventProc> _keepAlive = new();
    uint _tid;
    object? _shell;
    FolderState? _last;
    string _pendingSignal = "";
    bool _evalQueued;

    public Tracker()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "ExplorerTracker" };
        _thread.SetApartmentState(ApartmentState.STA);
    }

    public void Start() { _thread.Start(); _ready.Wait(); }
    public void Dispose() => Native.PostThreadMessageW(_tid, Native.WM_QUIT, 0, 0);
    public FolderState? Current => Volatile.Read(ref _last);

    void Run()
    {
        _tid = Native.GetCurrentThreadIdK();
        _shell = Com.Create("Shell.Application");
        var hooks = new List<IntPtr>();
        if (EnableEvents)
        {
            hooks.Add(Hook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, "foreground"));
            hooks.Add(Hook(Native.EVENT_SYSTEM_MINIMIZESTART, Native.EVENT_SYSTEM_MINIMIZEEND, "minimize"));
            hooks.Add(Hook(Native.EVENT_OBJECT_SHOW, Native.EVENT_OBJECT_HIDE, "show/hide"));
            hooks.Add(Hook(Native.EVENT_OBJECT_NAMECHANGE, Native.EVENT_OBJECT_NAMECHANGE, "namechange"));
        }
        if (EnablePoll) Native.SetTimer(IntPtr.Zero, 0, PollMs, IntPtr.Zero);
        Evaluate("initial");
        _ready.Set();

        while (Native.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == Native.WM_APP) { _evalQueued = false; Evaluate(_pendingSignal); continue; }
            if (msg.message == Native.WM_TIMER && msg.hwnd == IntPtr.Zero)
            {
                // Poll only while an Explorer frame is foreground (or to notice it lost foreground).
                if (Native.IsExplorerFrame(Native.GetForegroundWindow()) || _last?.Path != null) Evaluate("poll");
                continue;
            }
            Native.TranslateMessage(ref msg);
            Native.DispatchMessageW(ref msg);
        }
        foreach (var h in hooks) Native.UnhookWinEvent(h);
    }

    IntPtr Hook(uint min, uint max, string name)
    {
        Native.WinEventProc proc = (_, ev, hwnd, idObject, idChild, _, _) =>
        {
            if (idObject != 0 || idChild != 0) return;                     // window-level only
            if (ev is Native.EVENT_OBJECT_SHOW or Native.EVENT_OBJECT_HIDE or Native.EVENT_OBJECT_NAMECHANGE)
            {
                var root = Native.GetAncestor(hwnd, 2 /*GA_ROOT*/);
                if (!Native.IsExplorerFrame(root)) return;                   // cheap filter
                if (ev != Native.EVENT_OBJECT_NAMECHANGE && Native.ClassName(hwnd) != "ShellTabWindowClass") return;
            }
            _pendingSignal = name;
            if (!_evalQueued) { _evalQueued = true; Native.PostThreadMessageW(_tid, Native.WM_APP, 0, 0); }
        };
        _keepAlive.Add(proc);
        return Native.SetWinEventHook(min, max, IntPtr.Zero, proc, 0, 0, Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
    }

    void Evaluate(string signal)
    {
        var sw = Stopwatch.StartNew();
        var fg = Native.GetForegroundWindow();
        string? path = null;
        string method = "-";
        if (Native.IsExplorerFrame(fg) && !Native.IsIconic(fg))
        {
            (path, method) = ActiveTabPath(fg);
        }
        else fg = IntPtr.Zero;
        sw.Stop();
        EvalCosts.Add(sw.Elapsed.TotalMilliseconds);

        if (_last is not null && _last.Frame == fg && string.Equals(_last.Path, path, StringComparison.OrdinalIgnoreCase)) return;
        var st = new FolderState(fg, path, signal, Stopwatch.GetTimestamp(), sw.Elapsed.TotalMilliseconds, method);
        Volatile.Write(ref _last, st);
        Changed?.Invoke(st);
    }

    /// Returns the path of the active tab. Compares two active-tab heuristics:
    ///  A = first ShellTabWindowClass child in z-order; B = the visible ShellTabWindowClass child.
    (string? path, string method) ActiveTabPath(IntPtr frame)
    {
        var firstTab = Native.FindWindowExW(frame, IntPtr.Zero, "ShellTabWindowClass", null);
        IntPtr visibleTab = IntPtr.Zero;
        int tabCount = 0;
        for (var t = firstTab; t != IntPtr.Zero; t = Native.FindWindowExW(frame, t, "ShellTabWindowClass", null))
        {
            tabCount++;
            if (Native.IsWindowVisible(t)) visibleTab = visibleTab == IntPtr.Zero ? t : visibleTab;
        }
        if (firstTab != visibleTab) TabMethodDisagreements++;

        var candidates = new List<(IntPtr tab, string? path)>();
        foreach (var w in Com.Browsers(_shell!))
        {
            try
            {
                if (Com.Hwnd(w) != frame) continue;
                candidates.Add((TabHwnd(w), FolderPath(w)));
            }
            catch (System.Reflection.TargetInvocationException) { /* window closing */ }
        }

        var hit = candidates.FirstOrDefault(c => c.tab == visibleTab && visibleTab != IntPtr.Zero);
        if (hit.tab != IntPtr.Zero) return (hit.path, $"visible({tabCount} tabs)");
        hit = candidates.FirstOrDefault(c => c.tab == firstTab && firstTab != IntPtr.Zero);
        if (hit.tab != IntPtr.Zero) return (hit.path, $"first-z({tabCount} tabs)");
        return candidates.Count == 1 ? (candidates[0].path, "single") : (null, $"unresolved({candidates.Count})");
    }

    static IntPtr TabHwnd(object browser)
    {
        if (browser is not IServiceProvider sp) return IntPtr.Zero;
        var sid = SID_STopLevelBrowser; var iid = IID_IShellBrowser;
        if (sp.QueryService(ref sid, ref iid, out var p) != 0 || p == IntPtr.Zero) return IntPtr.Zero;
        try
        {
            var ow = (IOleWindow)Marshal.GetObjectForIUnknown(p);
            return ow.GetWindow(out var h) == 0 ? h : IntPtr.Zero;
        }
        finally { Marshal.Release(p); }
    }

    static string? FolderPath(object browser)
    {
        try
        {
            var p = Com.RawPath(browser);
            // Virtual folders return "::{GUID}" or shell namespace strings: not a file system path.
            if (string.IsNullOrEmpty(p) || p.StartsWith("::") || !(p.Length >= 3 && (p[1] == ':' || p.StartsWith(@"\\")))) return null;
            return p;
        }
        catch (System.Reflection.TargetInvocationException) { return null; }
        catch (COMException) { return null; }
    }
}
