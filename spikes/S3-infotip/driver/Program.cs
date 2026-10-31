// Spike S3 driver. Throwaway code.
//   S3Driver bench   -> in-process CoCreateInstance of the handler, latency of Load+GetInfoTip
//   S3Driver hover   -> opens Explorer, hovers a folder in several views, reads the tooltip via UIA
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Automation;

static class Program
{
    static readonly Guid Clsid = new("027D38C1-1502-4DF3-A632-A6A30EDF7876");
    static readonly string Log = Path.Combine(Path.GetTempPath(), "fn-s3.log");

    [STAThread]
    static int Main(string[] args) => (args.FirstOrDefault() ?? "bench") switch
    {
        "bench" => Bench(),
        "hover" => Hover(),
        "shot" => Shot(args.Skip(1).FirstOrDefault() ?? "Details", args.Skip(2).FirstOrDefault() ?? "shot.png"),
        _ => 2,
    };

    static int Bench()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "fn-s3", "Hover Target")).FullName;
        var type = Type.GetTypeFromCLSID(Clsid)!;
        var times = new List<double>();
        string? tip = null;
        for (int i = 0; i < 500; i++)
        {
            var sw = Stopwatch.StartNew();
            var o = Activator.CreateInstance(type)!;
            ((IPersistFile)o).Load(dir, 0);
            ((IQueryInfo)o).GetInfoTip(0, out var p);
            tip = Marshal.PtrToStringUni(p);
            Marshal.FreeCoTaskMem(p);
            Marshal.ReleaseComObject(o);
            times.Add(sw.Elapsed.TotalMilliseconds);
        }
        times.Sort();
        Console.WriteLine($"create+Load+GetInfoTip: first {times[^1]:F3} ms (max), median {times[times.Count / 2]:F3} ms, p99 {times[(int)(times.Count * 0.99)]:F3} ms");
        Console.WriteLine("tip text:\n" + tip);
        return 0;
    }

    static int Hover()
    {
        var parent = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "fn-s3")).FullName;
        foreach (var old in Directory.GetDirectories(parent)) Directory.Delete(old, true);
        foreach (var v in new[] { "Details", "Large icons", "List", "Tiles", "Content", "Small icons" })
        {
            Directory.CreateDirectory(Path.Combine(parent, "T " + v));
            File.WriteAllText(Path.Combine(parent, "T " + v, "a.txt"), "x");
        }
        File.Delete(Log);

        var frame = OpenExplorer(parent);
        var views = new (string name, int mode)[] { ("Details", 4), ("Large icons", 1), ("List", 3), ("Tiles", 6), ("Content", 8), ("Small icons", 2) };
        var rows = new List<string>();
        try
        {
            foreach (var (viewName, mode) in views)
            {
                var name = viewName;
                Win.ForceForeground(frame); Thread.Sleep(250);
                var list = AutomationElement.FromHandle(frame).FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.List));
                try { list?.SetFocus(); } catch { }                           // keys must go to the item view
                Thread.Sleep(200);
                SetViewMode(frame, mode); Thread.Sleep(900);           // Ctrl+Shift+<n> switches view
                Win.SetCursorPos(5, 5); Thread.Sleep(400);                 // reset hover
                int before = LogLines().Count(l => l.Contains("GetInfoTip"));

                var item = FindItem(frame, "T " + viewName);
                var visibleBefore = Win.VisibleTopLevel().ToHashSet();
                if (item is null) { rows.Add($"| {name} | item not found via UIA | | |"); continue; }
                var r = item.Current.BoundingRectangle;
                bool hasHeader = AutomationElement.FromHandle(frame).FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Header)) is not null;
                name = $"{name} (header={(hasHeader ? "yes" : "no")}, item {r.Width:F0}x{r.Height:F0})";
                int cx = (int)(r.Left + Math.Min(r.Width / 2, 40)), cy = (int)(r.Top + r.Height / 2);
                Win.SetCursorPos(cx - 30, cy);                               // approach like a real mouse
                for (int s = 0; s < 6; s++) { Win.mouse_event(1 /*MOVE*/, 5, 0, 0, 0); Thread.Sleep(30); }
                Win.SetCursorPos(cx, cy);
                var tipText = WaitForTooltip(1800);
                var tipHwnd = Win.TopLevel("tooltips_class32").FirstOrDefault(Win.IsWindowVisible);
                if (tipHwnd != IntPtr.Zero && Environment.GetEnvironmentVariable("S3_SHOTS") is { } shotDir)
                {
                    var tr = Win.Rect(tipHwnd);
                    int x = Math.Max(0, Math.Min(tr.Left, (int)r.Left) - 10), y = Math.Max(0, Math.Min(tr.Top, (int)r.Top) - 10);
                    int w = Math.Max(tr.Right, (int)r.Right) - x + 10, h = Math.Max(tr.Bottom, (int)r.Bottom) - y + 10;
                    using var bmp = new System.Drawing.Bitmap(w, h);
                    using (var g = System.Drawing.Graphics.FromImage(bmp)) g.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(w, h));
                    bmp.Save(Path.Combine(shotDir, $"s3-{viewName.Replace(' ', '-')}.png"), System.Drawing.Imaging.ImageFormat.Png);
                }
                var popups = Win.VisibleTopLevel().Except(visibleBefore).Select(h => $"{Win.Class(h)}:'{Flat(Win.Text(h))}'").ToList();
                Console.WriteLine($"[{name}] new visible windows: {string.Join(", ", popups)}");
                var calls = LogLines().Where(l => l.Contains("GetInfoTip")).Skip(before).ToList();
                bool ours = tipText?.Contains("S3 spike") == true;
                rows.Add($"| {name} | {(calls.Count > 0 ? "yes" : "no")} | {(ours ? "ours" : tipText is null ? "no tooltip seen" : "default only")} | {Flat(tipText)} |");
            }
        }
        finally
        {
            Win.ForceForeground(frame); Thread.Sleep(200);
            Win.Chord(0x11, 0x10, 0x36);                                     // back to Details
            Thread.Sleep(300);
            Win.PostMessageW(frame, 0x0010, 0, 0);
        }

        Console.WriteLine("| View | GetInfoTip called | Tooltip shown | Tooltip text |");
        Console.WriteLine("|---|---|---|---|");
        rows.ForEach(Console.WriteLine);
        Console.WriteLine();
        Console.WriteLine("Log tail:");
        foreach (var l in LogLines().TakeLast(8)) Console.WriteLine("  " + l);
        return 0;
    }

    /// Hover one folder in one view and screenshot the tooltip (plus surroundings) to a PNG.
    static int Shot(string view, string outPng)
    {
        var digits = new Dictionary<string, byte> { ["Details"] = 0x36, ["Large icons"] = 0x32, ["List"] = 0x35, ["Tiles"] = 0x37, ["Content"] = 0x38, ["Medium icons"] = 0x33 };
        var parent = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "fn-s3")).FullName;
        var name = "Shot " + DateTime.Now.ToString("HHmmss");
        Directory.CreateDirectory(Path.Combine(parent, name));
        var frame = OpenExplorer(parent);
        try
        {
            Win.ForceForeground(frame); Thread.Sleep(300);
            Win.Chord(0x11, 0x10, digits[view]); Thread.Sleep(1000);
            var item = FindItem(frame, name);
            if (item is null)
            {
                var all = AutomationElement.FromHandle(frame).FindAll(TreeScope.Descendants,
                    new OrCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
                                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataItem)));
                Console.WriteLine($"item '{name}' not found; title='{Win.Text(frame)}' items: " + string.Join(" | ", all.Cast<AutomationElement>().Select(e => e.Current.Name)));
                return 1;
            }
            var r = item.Current.BoundingRectangle;
            Console.WriteLine($"item rect {r}");
            int cx = (int)(r.Left + Math.Min(r.Width / 2, 40)), cy = (int)(r.Top + r.Height / 2);
            Win.SetCursorPos(cx - 30, cy);
            for (int s = 0; s < 6; s++) { Win.mouse_event(1, 5, 0, 0, 0); Thread.Sleep(30); }
            Win.SetCursorPos(cx, cy);
            IntPtr tip = IntPtr.Zero;
            for (int i = 0; i < 40 && tip == IntPtr.Zero; i++)
            {
                Thread.Sleep(100);
                tip = Win.TopLevel("tooltips_class32").FirstOrDefault(Win.IsWindowVisible);
            }
            Thread.Sleep(500);
            var rect = tip != IntPtr.Zero ? Win.Rect(tip) : new Win.RECT { Left = cx, Top = cy, Right = cx + 400, Bottom = cy + 200 };
            int x = Math.Max(0, Math.Min(rect.Left, (int)r.Left) - 20), y = Math.Max(0, Math.Min(rect.Top, (int)r.Top) - 20);
            int w = Math.Max(rect.Right, (int)r.Right) - x + 20, h = Math.Max(rect.Bottom, (int)r.Bottom) - y + 20;
            using var bmp = new System.Drawing.Bitmap(w, h);
            using (var g = System.Drawing.Graphics.FromImage(bmp)) g.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(w, h));
            bmp.Save(outPng, System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine($"tooltip {(tip == IntPtr.Zero ? "NOT found" : $"found {rect.Right - rect.Left}x{rect.Bottom - rect.Top}")}; saved {outPng}");
            return 0;
        }
        finally { Win.PostMessageW(frame, 0x0010, 0, 0); }
    }

    static void SetViewMode(IntPtr frame, int mode)
    {
        var shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
        var ws = shell.GetType().InvokeMember("Windows", System.Reflection.BindingFlags.InvokeMethod, null, shell, null)!;
        int n = (int)ws.GetType().InvokeMember("Count", System.Reflection.BindingFlags.GetProperty, null, ws, null)!;
        for (int i = 0; i < n; i++)
        {
            var w = ws.GetType().InvokeMember("Item", System.Reflection.BindingFlags.InvokeMethod, null, ws, [i]);
            if (w is null) continue;
            if ((IntPtr)Convert.ToInt64(w.GetType().InvokeMember("HWND", System.Reflection.BindingFlags.GetProperty, null, w, null)) != frame) continue;
            var doc = w.GetType().InvokeMember("Document", System.Reflection.BindingFlags.GetProperty, null, w, null)!;
            doc.GetType().InvokeMember("CurrentViewMode", System.Reflection.BindingFlags.SetProperty, null, doc, [mode]);
        }
    }

    static string Flat(string? s) => s is null ? "" : s.Replace("\r", "").Replace("\n", " ⏎ ").Replace("|", "/");
    static string[] LogLines() { try { return File.ReadAllLines(Log); } catch { return []; } }

    static AutomationElement? FindItem(IntPtr frame, string name)
    {
        var root = AutomationElement.FromHandle(frame);
        var cond = new AndCondition(
            new PropertyCondition(AutomationElement.NameProperty, name),
            new OrCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataItem)));
        for (int i = 0; i < 40; i++)
        {
            var e = root.FindFirst(TreeScope.Descendants, cond);
            if (e is not null) return e;
            Thread.Sleep(200);
        }
        return null;
    }

    static string? WaitForTooltip(int ms)
    {
        var sw = Stopwatch.StartNew();
        string? best = null;
        while (sw.ElapsedMilliseconds < ms)
        {
            foreach (AutomationElement t in AutomationElement.RootElement.FindAll(TreeScope.Children,
                         new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ToolTip)))
            {
                var n = t.Current.Name;
                if (!string.IsNullOrWhiteSpace(n)) best = n;
            }
            foreach (var h in Win.TopLevel("tooltips_class32"))
            {
                if (!Win.IsWindowVisible(h)) continue;
                var txt = Win.Text(h);
                if (!string.IsNullOrWhiteSpace(txt)) best = txt;
            }
            if (best is not null && sw.ElapsedMilliseconds > 900) return best;  // let the full tip render
            Thread.Sleep(100);
        }
        return best;
    }

    static IntPtr OpenExplorer(string path)
    {
        var before = Win.TopLevel("CabinetWClass").ToHashSet();
        Process.Start("explorer.exe", $"\"{path}\"");
        for (int i = 0; i < 100; i++)
        {
            Thread.Sleep(100);
            var f = Win.TopLevel("CabinetWClass").Except(before).FirstOrDefault();
            if (f != IntPtr.Zero) { Thread.Sleep(600); Win.SetWindowPos(f, IntPtr.Zero, 100, 100, 900, 600, 4); return f; }
        }
        throw new TimeoutException("Explorer window did not appear");
    }
}

[ComImport, Guid("00021500-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IQueryInfo
{
    [PreserveSig] int GetInfoTip(uint flags, out IntPtr tip);
    [PreserveSig] int GetInfoFlags(out uint flags);
}

[ComImport, Guid("0000010b-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IPersistFile
{
    void GetClassID(out Guid clsid);
    [PreserveSig] int IsDirty();
    void Load([MarshalAs(UnmanagedType.LPWStr)] string file, uint mode);
    void Save([MarshalAs(UnmanagedType.LPWStr)] string? file, bool remember);
    void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string file);
    void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string file);
}

static class Win
{
    [DllImport("user32", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowExW(IntPtr p, IntPtr after, string? cls, string? title);
    [DllImport("user32", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, System.Text.StringBuilder sb, int max);
    [DllImport("user32")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32")] public static extern void mouse_event(uint f, int dx, int dy, uint data, nint extra);
    [DllImport("user32")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
    [DllImport("user32")] public static extern bool PostMessageW(IntPtr h, uint m, nint w, nint l);
    [DllImport("user32")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    public static IEnumerable<IntPtr> TopLevel(string cls)
    {
        for (var h = FindWindowExW(IntPtr.Zero, IntPtr.Zero, cls, null); h != IntPtr.Zero; h = FindWindowExW(IntPtr.Zero, h, cls, null))
            yield return h;
    }
    [DllImport("user32", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, System.Text.StringBuilder sb, int max);
    public static string Class(IntPtr h) { var sb = new System.Text.StringBuilder(256); GetClassNameW(h, sb, 256); return sb.ToString(); }
    public static IEnumerable<IntPtr> VisibleTopLevel()
    {
        for (var h = FindWindowExW(IntPtr.Zero, IntPtr.Zero, null, null); h != IntPtr.Zero; h = FindWindowExW(IntPtr.Zero, h, null, null))
            if (IsWindowVisible(h)) yield return h;
    }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    public static RECT Rect(IntPtr h) { GetWindowRect(h, out var r); return r; }
    public static string Text(IntPtr h) { var sb = new System.Text.StringBuilder(4096); GetWindowTextW(h, sb, 4096); return sb.ToString(); }
    public static void ForceForeground(IntPtr h) { keybd_event(0x12, 0, 0, 0); keybd_event(0x12, 0, 2, 0); SetForegroundWindow(h); }
    public static void Chord(params byte[] vks)
    {
        foreach (var v in vks) keybd_event(v, 0, 0, 0);
        foreach (var v in vks.Reverse()) keybd_event(v, 0, 2, 0);
    }
}
