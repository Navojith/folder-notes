using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;

/// P3: path-read parity (route A GetCurFolder, route B LocationURL, route C = S1 late-bound Document.Folder.Self.Path,
/// reference only) over normal and virtual folders, and per-read cost.
static class P3
{
    static object? Get(object o, string n) => o.GetType().InvokeMember(n, BindingFlags.GetProperty, null, o, null);
    static object? Call(object o, string n, params object?[] a) => o.GetType().InvokeMember(n, BindingFlags.InvokeMethod, null, o, a);

    static string? RouteC(object shell, int index)
    {
        try
        {
            var ws = Call(shell, "Windows")!; var w = Call(ws, "Item", index); if (w is null) return "(no item)";
            var doc = Get(w, "Document"); var folder = doc is null ? null : Get(doc, "Folder"); var self = folder is null ? null : Get(folder, "Self");
            return self is null ? "(null)" : (string?)Get(self, "Path");
        }
        catch (Exception e) { return "(" + (e.InnerException ?? e).GetType().Name + ")"; }
    }

    public static int Run()
    {
        Harness.OnSta(() => { Go(); return 0; });
        return 0;
    }

    static void Go()
    {
        string root = Path.Combine(Path.GetTempPath(), "fn-s5-p3");
        Directory.CreateDirectory(Path.Combine(root, "Ünï 项目 📁")); Directory.CreateDirectory(Path.Combine(root, "plain folder"));
        string zip = Path.Combine(root, "a.zip");
        if (!File.Exists(zip)) { using var z = ZipFile.Open(zip, ZipArchiveMode.Create); z.CreateEntry("inner/readme.txt"); }
        var cases = new (string name, string arg)[]
        {
            ("plain", Path.Combine(root, "plain folder")),
            ("unicode", Path.Combine(root, "Ünï 项目 📁")),
            ("known folder (Downloads)", "shell:Downloads"),
            ("This PC", "shell:MyComputerFolder"),
            ("Home", "shell:::{F874310E-B6B7-47DC-BC84-B9E6B38F5903}"),
            ("Quick Access", "shell:::{679f85cb-0220-4080-b29b-5540cc05aab6}"),
            ("Recycle Bin", "shell:RecycleBinFolder"),
            ("Libraries", "shell:Libraries"),
            ("search results", "search-ms:displayname=Search&crumb=name:plain&crumb=location:" + root),
            ("zip folder", zip),
            ("UNC admin share", @"\\localhost\C$\Windows"),
            ("drive root", @"C:\"),
        };
        var root2 = Harness.CreateRoot();
        var shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
        Console.WriteLine($"{"case",-26} {"A: GetCurFolder",-52} {"B: LocationURL",-52} C: late-bound (S1)");
        var costA = new List<double>(); var costB = new List<double>();
        foreach (var (name, arg) in cases)
        {
            var (tab, all, ms) = Harness.Open(root2, arg);
            if (tab is null) { Console.WriteLine($"{name,-26} (no new window within timeout, {arg})"); continue; }
            var a = Harness.ReadPathA(tab); var b = Harness.ReadPathB(tab); var c = RouteC(shell, tab.Index);
            string fa = a.Path ?? $"null [{a.Stage} {Com.Hr(a.Hr)}]", fb = b.Path ?? $"null [{b.Stage}]";
            Console.WriteLine($"{name,-26} {Trunc(fa, 52),-52} {Trunc(fb, 52),-52} {c}");

            // cost: 200 reads of each on the cached tab (steady state)
            double Time(Func<PathRead> f) { f(); var sw = Stopwatch.StartNew(); for (int i = 0; i < 200; i++) f(); return sw.Elapsed.TotalMilliseconds / 200; }
            double ta = Time(() => Harness.ReadPathA(tab)), tb = Time(() => Harness.ReadPathB(tab));
            costA.Add(ta); costB.Add(tb);
            int tabsInFrame = all.Count(t => t.Frame == tab.Frame);
            if (tabsInFrame == 1) Harness.CloseFrame(tab.Frame); else Console.WriteLine("   (opened as a tab in an existing window; left open)");
            Harness.ReleaseTabs(all);
            Thread.Sleep(400);
        }
        // late-bound enumeration cost for context (S1: avg 34 ms)
        var sw2 = Stopwatch.StartNew(); for (int i = 0; i < 20; i++) RouteC(shell, 0); double c0 = sw2.Elapsed.TotalMilliseconds / 20;
        var sw3 = Stopwatch.StartNew(); for (int i = 0; i < 200; i++) { var ts = Harness.Tabs(root2); Harness.ReleaseTabs(ts); } double enumMs = sw3.Elapsed.TotalMilliseconds / 200;
        costA.Sort(); costB.Sort();
        Console.WriteLine($"\ncost per read (cached tab): route A median {costA[costA.Count / 2]:F3} ms, max {costA[^1]:F3}; route B median {costB[costB.Count / 2]:F3} ms, max {costB[^1]:F3}");
        Console.WriteLine($"late-bound route C one read: {c0:F2} ms; typed full enumeration of all tabs (Item+HWND+QueryService): {enumMs:F3} ms");
        Harness.Release(root2);
    }

    static string Trunc(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";
}
