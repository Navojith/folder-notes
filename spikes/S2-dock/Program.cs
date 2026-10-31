// Spike S2: a WinUI 3 tool window docked to (and owned by) an Explorer window. Throwaway code.
// Usage: S2Dock auto   -> scripted checks, prints a results table, exits
//        S2Dock watch  -> docks to a fresh Explorer window; try dragging/snapping by hand
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.XamlTypeInfo;

public static class Program
{
    public static string Mode = "auto";

    [STAThread]
    static void Main(string[] args)
    {
        Mode = args.FirstOrDefault() ?? "auto";
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(p =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            new App();
        });
    }
}

public sealed class App : Application, IXamlMetadataProvider
{
    readonly XamlControlsXamlMetaDataProvider _meta = new();
    public IXamlType GetXamlType(Type type) => _meta.GetXamlType(type);
    public IXamlType GetXamlType(string fullName) => _meta.GetXamlType(fullName);
    public XmlnsDefinition[] GetXmlnsDefinitions() => _meta.GetXmlnsDefinitions();

    Panel? _panel;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Resources.MergedDictionaries.Add(new XamlControlsResources());
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "fn-s2", "Client Acme Ltd")).FullName;
        var explorer = Win32.OpenExplorer(dir);
        _panel = new Panel(explorer);
        _panel.Attach();
        if (Program.Mode == "auto")
            new Thread(() => new Driver(_panel, explorer).Run()) { IsBackground = true }.Start();
        else if (Program.Mode == "debug") _panel.DebugRun();
    }
}

/// The docked panel window.
public sealed class Panel
{
    public readonly Window Window;
    public readonly IntPtr Hwnd;
    public IntPtr Owner { get; private set; }
    public string LastMode = "";
    public int RepositionCount;
    public bool Destroyed;
    readonly List<Win32.WinEventProc> _keep = new();
    const int WidthDip = 300;

    public Panel(IntPtr owner)
    {
        Owner = owner;
        Window = new Window { Title = "Folder Notes panel", SystemBackdrop = new Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop() };
        var stack = new StackPanel { Padding = new Thickness(16), Spacing = 8 };
        stack.Children.Add(new TextBlock { Text = "📁 Client Acme Ltd", Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] });
        stack.Children.Add(new TextBlock { Text = "Client: Acme Ltd", Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"] });
        stack.Children.Add(new TextBlock { Text = "🔒 Keep · client, do-not-delete\n⏳ Keep until 1 Jan 2027", TextWrapping = TextWrapping.Wrap });
        stack.Children.Add(new TextBox { PlaceholderText = "Inline edit test…", AcceptsReturn = true, Height = 120 });
        Window.Content = stack;
        Hwnd = WinRT.Interop.WindowNative.GetWindowHandle(Window);
        Window.Closed += (_, _) => Destroyed = true;

        var presenter = Microsoft.UI.Windowing.OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(true, false);   // thin border, no caption
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        Window.AppWindow.SetPresenter(presenter);
        Window.AppWindow.IsShownInSwitchers = false;     // Alt+Tab / taskbar
    }

    public void Attach()
    {
        var ex = Win32.GetWindowLongPtrW(Hwnd, Win32.GWL_EXSTYLE);
        ex = (ex | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE) & ~Win32.WS_EX_APPWINDOW;
        Win32.SetWindowLongPtrW(Hwnd, Win32.GWL_EXSTYLE, ex);
        SetOwner(Owner);

        Win32.GetWindowThreadProcessId(Owner, out var pid);
        Hook(Win32.EVENT_OBJECT_LOCATIONCHANGE, Win32.EVENT_OBJECT_LOCATIONCHANGE, pid);
        Hook(Win32.EVENT_SYSTEM_MOVESIZESTART, Win32.EVENT_SYSTEM_MINIMIZEEND, pid);
        Hook(Win32.EVENT_OBJECT_DESTROY, Win32.EVENT_OBJECT_DESTROY, pid);
        Reposition();
        Window.AppWindow.Show(false);                    // show WITHOUT activating
        Reposition();
    }

    public void SetOwner(IntPtr owner)
    {
        Owner = owner;
        Win32.SetWindowLongPtrW(Hwnd, Win32.GWLP_HWNDPARENT, owner);
    }

    void Hook(uint min, uint max, uint pid)
    {
        Win32.WinEventProc proc = (_, ev, hwnd, idObject, idChild, _, _) =>
        {
            if (hwnd != Owner || idObject != 0 || idChild != 0) return;
            if (ev == Win32.EVENT_OBJECT_DESTROY) { SetOwner(IntPtr.Zero); Window.AppWindow.Hide(); return; }
            Reposition();
        };
        _keep.Add(proc);
        Win32.SetWinEventHook(min, max, IntPtr.Zero, proc, pid, 0, Win32.WINEVENT_OUTOFCONTEXT);
    }

    /// Expected panel rect for the owner's current geometry (physical px). Also used by the driver.
    public (Win32.RECT rect, string mode) Expected()
    {
        var frame = Win32.FrameBounds(Owner);
        var work = Win32.WorkArea(Owner);
        int w = WidthDip * (int)Win32.GetDpiForWindow(Owner) / 96;
        if (frame.Right + w <= work.Right)
            return (new Win32.RECT { Left = frame.Right, Top = frame.Top, Right = frame.Right + w, Bottom = frame.Bottom }, "outside-right");
        if (frame.Left - w >= work.Left)
            return (new Win32.RECT { Left = frame.Left - w, Top = frame.Top, Right = frame.Left, Bottom = frame.Bottom }, "outside-left");
        int top = frame.Top + 96 * (int)Win32.GetDpiForWindow(Owner) / 96;   // below Explorer's toolbar
        return (new Win32.RECT { Left = frame.Right - w, Top = top, Right = frame.Right, Bottom = frame.Bottom }, "inside");
    }

    public void Reposition()
    {
        if (Owner == IntPtr.Zero || Win32.IsIconic(Owner)) return;   // owned windows hide with the owner
        var (r, mode) = Expected();
        // Compensate for the panel's own invisible DWM borders so visible edges touch exactly.
        var win = Win32.WindowRect(Hwnd); var vis = Win32.FrameBounds(Hwnd);
        int l = vis.Left - win.Left, t = vis.Top - win.Top, rr = win.Right - vis.Right, b = win.Bottom - vis.Bottom;
        Win32.SetWindowPos(Hwnd, IntPtr.Zero, r.Left - l, r.Top - t, r.Right - r.Left + l + rr, r.Bottom - r.Top + t + b,
            Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER);
        LastMode = mode;
        RepositionCount++;
        if (Program.Mode == "debug")
        {
            var w2 = Win32.WindowRect(Hwnd); var v2 = Win32.FrameBounds(Hwnd);
            Console.WriteLine($"{mode,-13} want {Fmt(r)}  win {Fmt(w2)}  vis {Fmt(v2)}  dpi {Win32.GetDpiForWindow(Hwnd)}/{Win32.GetDpiForWindow(Owner)}");
        }
    }

    static string Fmt(Win32.RECT r) => $"({r.Left},{r.Top})-({r.Right},{r.Bottom}) {r.Right - r.Left}x{r.Bottom - r.Top}";

    public void DebugRun()
    {
        new Thread(() =>
        {
            Thread.Sleep(1200);
            Win32.SetWindowPos(Owner, IntPtr.Zero, 100, 80, 700, 500, Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
            Thread.Sleep(800);
            Win32.SetWindowPos(Owner, IntPtr.Zero, 200, 120, 650, 560, Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
            Thread.Sleep(800);
            Win32.PostMessageW(Owner, 0x0010, 0, 0);
            Thread.Sleep(500);
            Environment.Exit(0);
        }) { IsBackground = true }.Start();
    }
}

/// Scripted checks, run on a background thread; reads window state via Win32 only.
public sealed class Driver(Panel panel, IntPtr explorer)
{
    readonly List<(string check, bool pass, string detail)> _rows = new();
    void Row(string c, bool p, string d = "") { _rows.Add((c, p, d)); Console.WriteLine($"{(p ? "ok  " : "FAIL")} {c}  {d}"); }

    public void Run()
    {
        Thread.Sleep(1500);
        try
        {
            Win32.ForceForeground(explorer); Thread.Sleep(400);
            Row("Explorer keeps foreground after panel shown", Win32.GetForegroundWindow() == explorer);
            var ex = Win32.GetWindowLongPtrW(panel.Hwnd, Win32.GWL_EXSTYLE);
            Row("Tool window, not app window (no Alt+Tab/taskbar)", (ex & Win32.WS_EX_TOOLWINDOW) != 0 && (ex & Win32.WS_EX_APPWINDOW) == 0);
            Row("Owner is Explorer", Win32.GetWindow(panel.Hwnd, 4 /*GW_OWNER*/) == explorer);

            // Programmatic moves/resizes: latency until the panel matches the expected rect.
            var work = Win32.WorkArea(explorer);
            var lat = new List<double>(); int maxGap = 0;
            var rnd = new Random(1);
            for (int i = 0; i < 12; i++)
            {
                int w = rnd.Next(600, 900), h = rnd.Next(400, work.Bottom - work.Top - 40);
                int x = rnd.Next(work.Left, Math.Max(work.Left + 1, work.Right - w - 420)), y = rnd.Next(work.Top, work.Bottom - h);
                var (ms, gap) = Measure(() => Win32.SetWindowPos(explorer, IntPtr.Zero, x, y, w, h, Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE));
                lat.Add(ms); maxGap = Math.Max(maxGap, gap);
            }
            lat.Sort();
            Row("Follows move/resize (12 runs)", lat[^1] < 100 && maxGap <= 1, $"median {lat[lat.Count / 2]:F1} ms, max {lat[^1]:F1} ms, max edge error {maxGap}px, mode={panel.LastMode}");

            Measure(() => Win32.ShowWindow(explorer, 3 /*SW_MAXIMIZE*/), settle: 600);
            Row("Maximized: docks inside", panel.LastMode == "inside" && Within(panel.Hwnd, explorer), $"mode={panel.LastMode}");
            Win32.ShowWindow(explorer, 9 /*SW_RESTORE*/); Thread.Sleep(500);

            Win32.ForceForeground(explorer); Thread.Sleep(200);
            Measure(() => Win32.SendChord(0x5B, 0x25), settle: 900);   // Win+Left snap
            Win32.SendChord(0x1B); Thread.Sleep(300);                  // Esc dismisses Snap Assist
            var g1 = Gap();
            Row("Snapped left: docks outside right", panel.LastMode == "outside-right" && g1 <= 1, $"mode={panel.LastMode}, edge error {g1}px");
            Win32.ForceForeground(explorer); Thread.Sleep(200);
            Measure(() => Win32.SendChord(0x5B, 0x27), settle: 900);   // Win+Right (from left-snap = restore)
            Measure(() => Win32.SendChord(0x5B, 0x27), settle: 900);   // Win+Right again = snap right
            Win32.SendChord(0x1B); Thread.Sleep(300);
            Row("Snapped right: docks outside left", panel.LastMode == "outside-left" && Gap() <= 1, $"mode={panel.LastMode}, edge error {Gap()}px");
            Win32.ShowWindow(explorer, 9); Thread.Sleep(400);
            Win32.SetWindowPos(explorer, IntPtr.Zero, work.Left + 50, work.Top + 50, 800, 600, Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE); Thread.Sleep(300);

            Win32.ShowWindow(explorer, 6 /*SW_MINIMIZE*/); Thread.Sleep(600);
            Row("Hidden while Explorer minimized", !Win32.IsWindowVisible(panel.Hwnd));
            Win32.ShowWindow(explorer, 9); Thread.Sleep(700);
            Row("Visible + docked after restore", Win32.IsWindowVisible(panel.Hwnd) && Gap() <= 1, $"edge error {Gap()}px");

            // Z-order: panel must sit above its owner, and below an unrelated window that is activated.
            Row("Panel above owner in z-order", Win32.IsAbove(panel.Hwnd, explorer));

            // Click-to-edit: drop NOACTIVATE, activate panel, check it can take focus.
            var exs = Win32.GetWindowLongPtrW(panel.Hwnd, Win32.GWL_EXSTYLE);
            Win32.SetWindowLongPtrW(panel.Hwnd, Win32.GWL_EXSTYLE, exs & ~Win32.WS_EX_NOACTIVATE);
            Win32.ForceForeground(panel.Hwnd); Thread.Sleep(300);
            Row("Panel can be activated for editing", Win32.GetForegroundWindow() == panel.Hwnd);
            Win32.ForceForeground(explorer); Thread.Sleep(300);
            Row("Panel stays visible when Explorer re-activated", Win32.IsWindowVisible(panel.Hwnd) && Win32.GetForegroundWindow() == explorer);
            Win32.SetWindowLongPtrW(panel.Hwnd, Win32.GWL_EXSTYLE, exs);

            // Owner closes: an owned window is destroyed with its owner unless we detach first.
            Win32.PostMessageW(explorer, 0x0010 /*WM_CLOSE*/, 0, 0); Thread.Sleep(1500);
            Row("Panel survives Explorer window closing", Win32.IsWindow(panel.Hwnd) && !panel.Destroyed,
                $"IsWindow={Win32.IsWindow(panel.Hwnd)} owner={panel.Owner}");
        }
        catch (Exception e) { Row("driver exception", false, e.Message); }

        Console.WriteLine();
        Console.WriteLine("| Check | Result | Detail |");
        Console.WriteLine("|---|---|---|");
        foreach (var r in _rows) Console.WriteLine($"| {r.check} | {(r.pass ? "PASS" : "FAIL")} | {r.detail} |");
        Environment.Exit(_rows.All(r => r.pass) ? 0 : 1);
    }

    (double ms, int gap) Measure(Action act, int settle = 0)
    {
        var sw = Stopwatch.StartNew();
        act();
        if (settle > 0) { Thread.Sleep(settle); return (0, Gap()); }
        while (sw.ElapsedMilliseconds < 1000)
        {
            if (Gap() <= 1) return (sw.Elapsed.TotalMilliseconds, Gap());
            Thread.SpinWait(2000);
        }
        return (1000, Gap());
    }

    /// Max distance (px) between the panel's visible rect and where it should be.
    int Gap()
    {
        var (exp, _) = panel.Expected();
        var vis = Win32.FrameBounds(panel.Hwnd);
        return new[] { vis.Left - exp.Left, vis.Top - exp.Top, vis.Right - exp.Right, vis.Bottom - exp.Bottom }.Select(Math.Abs).Max();
    }

    static bool Within(IntPtr inner, IntPtr outer)
    {
        var i = Win32.FrameBounds(inner); var o = Win32.FrameBounds(outer);
        return i.Left >= o.Left && i.Right <= o.Right && i.Top >= o.Top && i.Bottom <= o.Bottom;
    }
}

public static class Win32
{
    public const int GWL_EXSTYLE = -20, GWLP_HWNDPARENT = -8;
    public const long WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000, WS_EX_NOACTIVATE = 0x08000000;
    public const uint SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_NOOWNERZORDER = 0x200;
    public const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B, EVENT_OBJECT_DESTROY = 0x8001;
    public const uint EVENT_SYSTEM_MOVESIZESTART = 0x000A, EVENT_SYSTEM_MINIMIZEEND = 0x0017;
    public const uint WINEVENT_OUTOFCONTEXT = 0;

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    public delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32")] public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr hmod, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32")] public static extern long GetWindowLongPtrW(IntPtr h, int idx);
    [DllImport("user32")] public static extern long SetWindowLongPtrW(IntPtr h, int idx, long v);
    [DllImport("user32")] public static extern long SetWindowLongPtrW(IntPtr h, int idx, IntPtr v);
    [DllImport("user32")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("dwmapi")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
    [DllImport("user32")] static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32")] static extern bool GetMonitorInfoW(IntPtr m, ref MONITORINFO mi);
    [DllImport("user32")] public static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32")] public static extern bool PostMessageW(IntPtr h, uint msg, nint w, nint l);
    [DllImport("user32")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string? cls, string? title);

    public static RECT WindowRect(IntPtr h) { GetWindowRect(h, out var r); return r; }
    public static RECT FrameBounds(IntPtr h) => DwmGetWindowAttribute(h, 9 /*DWMWA_EXTENDED_FRAME_BOUNDS*/, out var r, 16) == 0 ? r : WindowRect(h);
    public static RECT WorkArea(IntPtr h)
    {
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfoW(MonitorFromWindow(h, 2), ref mi);
        return mi.rcWork;
    }

    public static bool IsAbove(IntPtr a, IntPtr b)
    {
        for (var h = GetWindow(a, 2 /*GW_HWNDNEXT*/); h != IntPtr.Zero; h = GetWindow(h, 2)) if (h == b) return true;
        return false;
    }

    public static void ForceForeground(IntPtr h)
    {
        if (IsIconic(h)) ShowWindow(h, 9);
        keybd_event(0x12, 0, 0, UIntPtr.Zero); keybd_event(0x12, 0, 2, UIntPtr.Zero);
        SetForegroundWindow(h);
    }

    public static void SendChord(params byte[] vks)
    {
        foreach (var vk in vks) keybd_event(vk, 0, vk == 0x5B || vk is 0x25 or 0x27 ? 1u : 0u, UIntPtr.Zero);
        foreach (var vk in vks.Reverse()) keybd_event(vk, 0, (vk == 0x5B || vk is 0x25 or 0x27 ? 1u : 0u) | 2u, UIntPtr.Zero);
    }

    static HashSet<IntPtr> Frames()
    {
        var s = new HashSet<IntPtr>();
        for (var h = FindWindowExW(IntPtr.Zero, IntPtr.Zero, "CabinetWClass", null); h != IntPtr.Zero; h = FindWindowExW(IntPtr.Zero, h, "CabinetWClass", null)) s.Add(h);
        return s;
    }

    public static IntPtr OpenExplorer(string path)
    {
        var before = Frames();
        Process.Start("explorer.exe", $"\"{path}\"");
        for (int i = 0; i < 100; i++)
        {
            Thread.Sleep(100);
            var f = Frames().Except(before).FirstOrDefault();
            if (f != IntPtr.Zero) { Thread.Sleep(400); SetWindowPos(f, IntPtr.Zero, 60, 60, 800, 600, SWP_NOZORDER); return f; }
        }
        throw new TimeoutException("Explorer window did not appear");
    }
}
