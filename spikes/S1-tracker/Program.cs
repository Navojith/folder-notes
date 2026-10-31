// Spike S1: track the active Explorer folder (incl. Windows 11 tabs). Throwaway code.
// Usage:  S1Tracker watch            -> print changes live (manual testing)
//         S1Tracker auto [--no-poll] -> scripted scenarios with latency measurements
using System.Diagnostics;

static class Program
{
    const byte VK_CONTROL = 0x11, VK_SHIFT = 0x10, VK_TAB = 0x09, VK_T = 0x54;

    [STAThread]
    static int Main(string[] args)
    {
        var mode = args.FirstOrDefault() ?? "watch";
        var tracker = new Tracker { EnablePoll = !args.Contains("--no-poll") };
        if (mode == "watch")
        {
            tracker.Changed += s => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [{s.Signal,-10}] {s.TabMethod,-16} {s.EvalMs,6:F2}ms  {s.Path ?? "(none)"}");
            tracker.Start();
            Console.WriteLine("Watching. Ctrl+C to quit.");
            Thread.Sleep(Timeout.Infinite);
        }
        return new AutoRun(tracker).Run();
    }

    sealed class AutoRun(Tracker tracker)
    {
        readonly object _shell = Com.Create("Shell.Application");
        readonly List<(string scenario, double ms, string signal, bool ok)> _rows = new();
        readonly object _gate = new();
        readonly List<IntPtr> _frames = new();

        public int Run()
        {
            var root = Path.Combine(Path.GetTempPath(), "fn-s1");
            string A = Mk(root, "Alpha"), B = Mk(root, "Bravo"), C = Mk(root, "Charlie"), D = Mk(root, "Delta");
            string S1 = Mk(root, @"One\src"), S2 = Mk(root, @"Two\src"), U = Mk(root, "Ünïcödé 📁 项目");

            tracker.Changed += _ => { lock (_gate) Monitor.PulseAll(_gate); };
            tracker.Start();
            try
            {
                var w1 = OpenWindow(A);
                Native.ForceForeground(w1);
                Expect("open window + foreground", () => { }, w1, A);

                for (int i = 0; i < 5; i++)
                {
                    Expect("navigate (different name)", () => Navigate(w1, B), w1, B);
                    Expect("navigate (different name)", () => Navigate(w1, A), w1, A);
                }
                for (int i = 0; i < 3; i++)
                {
                    Expect("navigate (same folder name)", () => Navigate(w1, S1), w1, S1);
                    Expect("navigate (same folder name)", () => Navigate(w1, S2), w1, S2);
                }
                Expect("navigate (unicode path)", () => Navigate(w1, U), w1, U);
                Expect("navigate to This PC (virtual)", () => Navigate(w1, "shell:::{20D04FE0-3AEA-1069-A2D8-08002B30309D}"), w1, null);
                Expect("navigate back to real folder", () => Navigate(w1, A), w1, A);

                // --- Tabs ---
                var before = TabsOf(w1);
                Native.ForceForeground(w1); Thread.Sleep(150);
                Expect("Ctrl+T new tab (Home, virtual)", () => Native.SendChord(VK_CONTROL, VK_T), w1, null);
                var newTab = WaitForNewTab(w1, before);
                Expect("navigate new tab", () => Navigate(w1, C, newTab), w1, C);
                for (int i = 0; i < 4; i++)
                {
                    Expect("switch tab (Ctrl+Shift+Tab)", () => Native.SendChord(VK_CONTROL, VK_SHIFT, VK_TAB), w1, A);
                    Expect("switch tab (Ctrl+Tab)", () => Native.SendChord(VK_CONTROL, VK_TAB), w1, C);
                }

                // --- Second window / foreground switches ---
                var w2 = OpenWindow(D);
                for (int i = 0; i < 4; i++)
                {
                    Expect("switch window", () => Native.ForceForeground(w2), w2, D);
                    Expect("switch window", () => Native.ForceForeground(w1), w1, C);
                }
                Expect("minimize foreground Explorer", () => Native.ShowWindow(w1, 6 /*SW_MINIMIZE*/), null, null);
            }
            finally
            {
                foreach (var f in _frames) CloseFrame(f);
                tracker.Dispose();
            }
            return Report();
        }

        static string Mk(string root, string rel) => Directory.CreateDirectory(Path.Combine(root, rel)).FullName;

        void Expect(string scenario, Action action, IntPtr? frame, string? path)
        {
            var start = Stopwatch.GetTimestamp();
            action();
            var deadline = start + Stopwatch.Frequency * 3;
            FolderState? s;
            lock (_gate)
            {
                while (true)
                {
                    s = tracker.Current;
                    bool frameOk = frame is null ? s?.Frame == IntPtr.Zero : s?.Frame == frame;
                    if (s is not null && frameOk && string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase)) break;
                    var left = deadline - Stopwatch.GetTimestamp();
                    if (left <= 0) { s = null; break; }
                    Monitor.Wait(_gate, TimeSpan.FromTicks(Math.Max(1, left * TimeSpan.TicksPerSecond / Stopwatch.Frequency)));
                }
            }
            var ms = s is null ? 3000 : (s.Ticks - start) * 1000.0 / Stopwatch.Frequency;
            _rows.Add((scenario, Math.Max(0, ms), s?.Signal ?? $"TIMEOUT (saw {tracker.Current?.Path ?? "none"})", s is not null));
            Console.WriteLine($"{(s is null ? "FAIL" : "ok  ")} {ms,7:F1} ms  {s?.Signal,-10}  {scenario} -> {path ?? "(none)"}");
            Thread.Sleep(120); // let Explorer settle between steps
        }

        IntPtr OpenWindow(string path)
        {
            var existing = AllFrames();
            Process.Start("explorer.exe", $"\"{path}\"");
            for (int i = 0; i < 100; i++)
            {
                Thread.Sleep(100);
                var f = AllFrames().Except(existing).FirstOrDefault();
                if (f != IntPtr.Zero) { _frames.Add(f); Thread.Sleep(300); Native.ForceForeground(f); return f; }
            }
            throw new TimeoutException("Explorer window did not appear");
        }

        HashSet<IntPtr> AllFrames()
        {
            var set = new HashSet<IntPtr>();
            foreach (var w in Com.Browsers(_shell)) set.Add(Com.Hwnd(w));
            return set;
        }

        List<object> TabsOf(IntPtr frame) => Com.Browsers(_shell).Where(b => Com.Hwnd(b) == frame).ToList();

        object WaitForNewTab(IntPtr frame, List<object> before)
        {
            for (int i = 0; i < 50; i++)
            {
                var now = TabsOf(frame);
                if (now.Count > before.Count)
                    return now.First(t => !before.Any(b => ReferenceEquals(b, t)));
                Thread.Sleep(50);
            }
            throw new TimeoutException("new tab not registered");
        }

        void Navigate(IntPtr frame, string path, object? tab = null)
        {
            if (tab is null)
            {
                // the active tab = the one the tracker resolved; simplest: navigate the tab whose doc path matches current
                var cur = tracker.Current?.Path;
                var tabs = TabsOf(frame);
                tab = tabs.FirstOrDefault(t => { try { return string.Equals(Com.RawPath(t), cur, StringComparison.OrdinalIgnoreCase); } catch { return false; } })
                      ?? tabs.First();
            }
            Com.Call(tab, "Navigate2", path);
        }

        void CloseFrame(IntPtr frame)
        {
            foreach (var t in TabsOf(frame)) { try { Com.Call(t, "Quit"); } catch { } }
        }

        int Report()
        {
            Console.WriteLine();
            Console.WriteLine($"poll={(tracker.EnablePoll ? tracker.PollMs + "ms" : "off")}  tab-heuristic disagreements={tracker.TabMethodDisagreements}");
            Console.WriteLine($"Evaluate() cost: avg {tracker.EvalCosts.Average():F2} ms, p95 {Pct(tracker.EvalCosts, 95):F2} ms, max {tracker.EvalCosts.Max():F2} ms (n={tracker.EvalCosts.Count})");
            Console.WriteLine();
            Console.WriteLine("| Scenario | n | pass | median ms | max ms | signals |");
            Console.WriteLine("|---|---|---|---|---|---|");
            foreach (var g in _rows.GroupBy(r => r.scenario))
            {
                var ms = g.Where(r => r.ok).Select(r => r.ms).ToList();
                Console.WriteLine($"| {g.Key} | {g.Count()} | {g.Count(r => r.ok)} | {(ms.Count > 0 ? Pct(ms, 50) : double.NaN):F0} | {(ms.Count > 0 ? ms.Max() : double.NaN):F0} | {string.Join(",", g.Select(r => r.signal).Distinct())} |");
            }
            return _rows.All(r => r.ok) ? 0 : 1;
        }

        static double Pct(List<double> xs, int p)
        {
            var s = xs.OrderBy(x => x).ToList();
            return s[Math.Clamp((int)Math.Ceiling(p / 100.0 * s.Count) - 1, 0, s.Count - 1)];
        }
    }
}
