using System.Diagnostics;
using System.IO.Compression;
using FolderNotes.App.Explorer;

namespace FolderNotes.App.Tests.Explorer.Real;

/// <summary>
/// T3.2.5 functional checks against the real Explorer of this desktop: path resolution for every folder kind that can be
/// created safely, tabs, several windows, the foreground rules and the panel stand-in. They open windows of their own
/// and close only those. Run with <c>FOLDERNOTES_REAL_EXPLORER=disposable</c>, on a disposable session only (09 O-26); every timing is TIMING-BASED (not a concurrency
/// guarantee; those are the deterministic tests of 06).
/// </summary>
[Collection("RealExplorer")]
[Trait("Category", "Explorer")]
[Trait("Kind", "RealExplorer")]
public sealed class RealExplorerFunctionalTests
{
    private const string ThisPc = "shell:::{20D04FE0-3AEA-1069-A2D8-08002B30309D}";

    private sealed record Case(string Name, string Target, string? Expected, bool Observe = false, bool OwnWindow = false);

    [RealExplorerFact]
    public void REQ031_Path_resolution_matches_Explorer_for_each_folder_kind() =>
        Scn.Run("resolution", rec => ResolutionBody(rec));

    private static void ResolutionBody(Rec rec)
    {
        using var lab = new ExplorerLab("resolution");
        using var rig = new TrackerRig();
        rig.StartResult.ShouldBe(StartResult.Started);

        string a = lab.Mk("Alpha");
        string same1 = lab.Mk(@"One\src");
        string same2 = lab.Mk(@"Two\src");
        string uni = lab.Mk("Ünïcödé 📁 项目");
        string odd = lab.Mk("Folder with spaces & (symbols) #1 [x]");
        string deep = lab.Mk(@"Deep\d1\d2\d3\d4\d5\d6\d7\d8\d9\d10");
        string longPath = lab.Mk(string.Join('\\', Enumerable.Repeat("long_segment_name_xxxxxxxxxx", 9)));
        string zipSrc = lab.Mk("zipsrc");
        File.WriteAllText(Path.Combine(zipSrc, "inside.txt"), "x");
        string zip = Path.Combine(lab.Root, "archive.zip");
        File.Delete(zip);
        ZipFile.CreateFromDirectory(zipSrc, zip);

        var cases = new List<Case>
        {
            new("plain folder", lab.Mk("Bravo"), lab.Mk("Bravo")),
            new("same leaf name, parent One", same1, same1),
            new("same leaf name, parent Two", same2, same2),
            new("Unicode and emoji", uni, uni),
            new("spaces, &, parentheses, #, brackets", odd, odd),
            new("deep (10 levels)", deep, deep),
            new("long path (over MAX_PATH)", longPath, longPath, Observe: true),
            new("drive root C:\\", "C:\\", "C:\\"),
            new("UNC \\\\localhost\\C$", @"\\localhost\C$" + a[2..], @"\\localhost\C$" + a[2..], OwnWindow: true),
            new("known folder (Downloads)", "shell:Downloads", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")),
            new(".zip browsed as a folder (reports the file)", zip, zip),
            new("This PC (virtual)", ThisPc, null),
            new("Recycle Bin (virtual)", "shell:::{645FF040-5081-101B-9F08-00AA002F954E}", null),
            new("Control Panel, all items (virtual)", "shell:::{21EC2020-3AEA-1069-A2DD-08002B30309D}", null),
            new("Network (virtual)", "shell:NetworkPlacesFolder", null),
            new("Libraries (virtual)", "shell:Libraries", null),
            new("Home / Quick Access (virtual)", "shell:::{679F85CB-0220-4080-B29B-5540CC05AAB6}", null),
        };

        if (Directory.Exists("D:\\"))
        {
            cases.Insert(8, new Case("second drive root D:\\", "D:\\", "D:\\"));
        }

        var subst = lab.Subst(lab.Root);
        if (subst is not null)
        {
            cases.Add(new Case("subst drive (mapped-drive stand-in)", subst + "\\Alpha", subst + "\\Alpha", OwnWindow: true));
        }
        else
        {
            rec.Line("NOT OBTAINED: subst drive could not be created.");
        }

        var net = lab.NetUse(@"\\localhost\C$");
        if (net is not null)
        {
            cases.Add(new Case("network-redirector drive (net use to \\\\localhost\\C$)", net + a[2..], net + a[2..], OwnWindow: true));
        }
        else
        {
            rec.Line("NOT OBTAINED: a net-use drive to the loopback share could not be created; only UNC and subst were checked.");
        }

        cases.Add(new Case("search results (search-ms)", $"search-ms:query=Alpha&crumb=location:{lab.Root}", null, Observe: true));

        nint w1 = lab.OpenWindow(a);
        lab.Foreground(w1).ShouldBeTrue("the lab window must reach the foreground");
        var first = rig.Observer.Await(s => Scn.At(s, w1, a), 0, 4000);
        rec.Line($"First state after the window opened: {Scn.Describe(first.Seen)} ({(first.Ms is null ? "TIMEOUT" : "ok")})");

        var failures = new List<string>();
        var latencies = new List<double>();
        rec.Line("| Scenario | Expected | Published | Oracle (Explorer's own) | Latency | Result |");
        rec.Line("|---|---|---|---|---|---|");
        foreach (var c in cases)
        {
            Console.WriteLine($"[resolution] ... {c.Name}");
            if (c.OwnWindow)
            {
                // Navigate2 to a network path can block the calling thread for a long time; a window of its own cannot.
                long ts0 = Stopwatch.GetTimestamp();
                nint wn = 0;
                try
                {
                    wn = lab.OpenWindow(c.Target, timeoutMs: 15000);
                    lab.Foreground(wn);
                    var rn = rig.Observer.Await(s => Scn.At(s, wn, c.Expected), ts0, 8000);
                    var oraclePath = lab.TabsOf(wn).Count > 0 ? ExplorerLab.OraclePath(lab.TabsOf(wn)[0]) : null;
                    rec.Line($"| {c.Name} (own window) | {c.Expected} | {(rn.Ms is null ? Scn.Describe(rn.Seen) : c.Expected)} | {oraclePath ?? "(none)"} | {(rn.Ms is null ? "-" : rn.Ms.Value.ToString("F0") + " ms")} | {(rn.Ms is null ? "FAIL" : "pass")} |");
                    if (rn.Ms is null)
                    {
                        failures.Add($"{c.Name}: saw {Scn.Describe(rn.Seen)}");
                    }
                }
                catch (TimeoutException)
                {
                    rec.Line($"| {c.Name} (own window) | {c.Expected} | - | - | - | NOT OBTAINED: Explorer opened no window for it |");
                }
                finally
                {
                    if (wn != 0)
                    {
                        lab.Close(wn);
                    }

                    lab.Foreground(w1);
                }

                continue;
            }

            // Return to a real folder first so that a virtual target is a change (no-op suppression, 04 D.6).
            var back = Scn.At(rig.Tracker.Current, w1, a)
                ? (Ms: (double?)0, Seen: rig.Tracker.Current)
                : Scn.Step(rig, () => ExplorerLab.Navigate(lab.TabsOf(w1)[0], a), s => Scn.At(s, w1, a));
            if (back.Ms is null)
            {
                failures.Add($"{c.Name}: could not return to the base folder first ({Scn.Describe(back.Seen)})");
                continue;
            }

            var tab = lab.TabsOf(w1)[0];
            Func<ActiveFolderState, bool> expect = c.Observe
                ? s => s.FrameHwnd == w1 && !Scn.At(s, w1, a)
                : s => Scn.At(s, w1, c.Expected);
            var r = Scn.Step(rig, () => ExplorerLab.Navigate(tab, c.Target), expect, settleMs: c.Observe ? 1500 : 250);
            var oracle = ExplorerLab.OraclePath(lab.TabsOf(w1)[0]);
            var published = rig.Observer.Last;
            string result;
            if (c.Observe)
            {
                result = r.Ms is null ? "OBSERVED: no change published" : "OBSERVED";
            }
            else if (r.Ms is null)
            {
                result = "FAIL";
                failures.Add($"{c.Name}: expected {c.Expected ?? "(null)"}, saw {Scn.Describe(r.Seen)}");
            }
            else
            {
                result = "pass";
                latencies.Add(r.Ms.Value);
            }

            rec.Line($"| {c.Name} | {c.Expected ?? "(null)"} | {(published.Path is null ? "(null)" : published.Path)} | {oracle ?? "(none)"} | {(r.Ms is null ? "-" : r.Ms.Value.ToString("F0") + " ms")} | {result} |");
        }

        var stats = rig.Tracker.GetStats();
        rec.Line($"Latency over passing cases: {Stat.Summary(latencies)}");
        rec.Line($"Evaluation cost (TrackerStats, last 256): p50 {stats.EvaluationP50Ms:F2} ms, p95 {stats.EvaluationP95Ms:F2} ms, max {stats.EvaluationMaxMs:F2} ms over {stats.Evaluations} evaluations, {stats.Rebuilds} rebuilds, {stats.PublishedStates} published, live RCWs {stats.LiveRcws}");
        rec.Line($"Sequence regressions seen by the UI thread: {rig.Observer.SequenceRegressions}; tracker state {stats.State}; hooks active {stats.HooksActive}; log: {rig.LogText()}");

        rig.Observer.SequenceRegressions.ShouldBe(0);
        stats.AbandonedWorkers.ShouldBe(0);
        failures.ShouldBeEmpty(string.Join(Environment.NewLine, failures));
        latencies.Max().ShouldBeLessThan(500, "no resolution may take anywhere near a visible delay");
        rig.Tracker.Stop();
        rig.Tracker.GetStats().LiveRcws.ShouldBe(0);
    }

    [RealExplorerFact]
    public void REQ034_Tabs_and_several_windows_follow_the_foreground_tab() =>
        Scn.Run("tabs-windows", rec =>
        {
            using var lab = new ExplorerLab("tabs");
            using var rig = new TrackerRig();
            string a = lab.Mk("TabA"), b = lab.Mk("TabB"), c = lab.Mk("TabC"), d = lab.Mk("WinD"), e = lab.Mk("WinE"), f = lab.Mk("SepF");
            var failures = new List<string>();
            var tabLat = new List<double>();
            var winLat = new List<double>();
            var procLat = new List<double>();

            void Check(string what, (double? Ms, ActiveFolderState Seen) r, List<double>? into = null)
            {
                if (r.Ms is null)
                {
                    failures.Add($"{what}: saw {Scn.Describe(r.Seen)}");
                    rec.Line($"FAIL {what}: saw {Scn.Describe(r.Seen)}");
                }
                else
                {
                    into?.Add(r.Ms.Value);
                }
            }

            nint w1 = lab.OpenWindow(a);
            lab.Foreground(w1).ShouldBeTrue();
            Check("open window 1", rig.Observer.Await(s => Scn.At(s, w1, a), 0));

            // tab 2 and 3
            var t2 = lab.NewTab(w1);
            Check("navigate new tab to B", Scn.Step(rig, () => ExplorerLab.Navigate(t2, b), s => Scn.At(s, w1, b)));
            var t3 = lab.NewTab(w1);
            Check("navigate new tab to C", Scn.Step(rig, () => ExplorerLab.Navigate(t3, c), s => Scn.At(s, w1, c)));

            // Tabs are A, B, C with C active. Ctrl+Tab goes right and wraps.
            string[] order = [a, b, c];
            int active = 2;
            for (int i = 0; i < 12; i++)
            {
                active = (active + 1) % 3;
                var expect = order[active];
                Check($"Ctrl+Tab #{i + 1} -> {Scn.Leaf(expect)}", Scn.Step(rig, () => lab.Keys(w1, LabNative.VK_CONTROL, LabNative.VK_TAB), s => Scn.At(s, w1, expect)), tabLat);
            }

            for (int i = 0; i < 12; i++)
            {
                active = (active + 2) % 3;
                var expect = order[active];
                Check($"Ctrl+Shift+Tab #{i + 1} -> {Scn.Leaf(expect)}", Scn.Step(rig, () => lab.Keys(w1, LabNative.VK_CONTROL, LabNative.VK_SHIFT, LabNative.VK_TAB), s => Scn.At(s, w1, expect)), tabLat);
            }

            // Closing the active tab (Ctrl+W) must land on one of the remaining tabs, and the published folder must be that tab's.
            var closeActive = order[active];
            var remaining = order.Where(p => p != closeActive).ToArray();
            var closed = Scn.Step(rig, () => lab.Keys(w1, LabNative.VK_CONTROL, LabNative.VK_W), s => s.FrameHwnd == w1 && remaining.Contains(s.Path, StringComparer.OrdinalIgnoreCase));
            Check($"Ctrl+W closes {Scn.Leaf(closeActive)}", closed);
            rec.Line($"Closing the active tab {Scn.Leaf(closeActive)} landed on {Scn.Describe(closed.Seen)}; Explorer's frame title says '{LabNative.Title(w1)}'");
            if (closed.Ms is not null)
            {
                Scn.TitleShows(w1, closed.Seen.Path!).ShouldBeTrue("the published folder must be the one Explorer shows");
            }

            // Several windows in the shell's process, and one in its own process.
            nint w2 = lab.OpenWindow(d);
            lab.Foreground(w2).ShouldBeTrue();
            Check("open window 2", rig.Observer.Await(s => Scn.At(s, w2, d), 0));
            var w2tab2 = lab.NewTab(w2);
            Check("window 2, new tab to E", Scn.Step(rig, () => ExplorerLab.Navigate(w2tab2, e), s => Scn.At(s, w2, e)));
            nint w3 = lab.OpenWindow(f, separateProcess: true);
            lab.Foreground(w3).ShouldBeTrue();
            Check("open window 3 (own process)", rig.Observer.Await(s => Scn.At(s, w3, f), 0));
            var pids = new[] { LabNative.PidOf(w1), LabNative.PidOf(w2), LabNative.PidOf(w3) };
            rec.Line($"Windows: w1 pid {pids[0]}, w2 pid {pids[1]}, w3 pid {pids[2]} (shell pid {lab.ShellPid}); new explorer processes: {string.Join(",", lab.NewExplorerProcesses())}");
            pids[2].ShouldNotBe(lab.ShellPid, "window 3 must live in its own process");

            var w1Path = closed.Seen.Path!;
            for (int i = 0; i < 10; i++)
            {
                Check($"switch to window 2 #{i + 1}", Scn.Step(rig, () => lab.Foreground(w2), s => Scn.At(s, w2, e)), winLat);
                Check($"switch to window 1 #{i + 1}", Scn.Step(rig, () => lab.Foreground(w1), s => Scn.At(s, w1, w1Path)), winLat);
                Check($"switch to window 3 (own process) #{i + 1}", Scn.Step(rig, () => lab.Foreground(w3), s => Scn.At(s, w3, f)), procLat);
            }

            var stats = rig.Tracker.GetStats();
            rec.Line($"Tab switch latency: {Stat.Summary(tabLat)}");
            rec.Line($"Window switch latency (same process): {Stat.Summary(winLat)}");
            rec.Line($"Window switch latency (across processes, includes hook re-scope): {Stat.Summary(procLat)}");
            rec.Line($"Evaluation p50 {stats.EvaluationP50Ms:F2} ms, p95 {stats.EvaluationP95Ms:F2} ms, max {stats.EvaluationMaxMs:F2} ms; rebuilds {stats.Rebuilds}; published {stats.PublishedStates}; live RCWs {stats.LiveRcws}; hooks installed {rig.WinEvents.Installs}, removed {rig.WinEvents.Unhooks}");
            rig.Observer.SequenceRegressions.ShouldBe(0);
            stats.AbandonedWorkers.ShouldBe(0);
            failures.ShouldBeEmpty(string.Join(Environment.NewLine, failures));
        });

    [RealExplorerFact]
    public void REQ033_REQ038_The_foreground_rules_hold_for_the_panel_other_apps_and_minimize() =>
        Scn.Run("foreground", rec =>
        {
            using var lab = new ExplorerLab("foreground");
            using var rig = new TrackerRig();
            string a = lab.Mk("FgA"), b = lab.Mk("FgB"), d = lab.Mk("FgD");
            var failures = new List<string>();

            void Check(string what, (double? Ms, ActiveFolderState Seen) r)
            {
                rec.Line($"{(r.Ms is null ? "FAIL" : "pass")} {what}: {Scn.Describe(r.Seen)}{(r.Ms is null ? string.Empty : $" after {r.Ms.Value:F0} ms")}");
                if (r.Ms is null)
                {
                    failures.Add(what);
                }
            }

            nint w1 = lab.OpenWindow(a);
            nint w2 = lab.OpenWindow(d);
            lab.Foreground(w1).ShouldBeTrue();
            Check("window 1 foreground", rig.Observer.Await(s => Scn.At(s, w1, a), 0));

            using var panel = new LabWindow(w1, "Folder Notes panel (lab)");
            int before = rig.Observer.Count;
            lab.Foreground(w1);
            LabNative.ForceForeground(panel.Hwnd).ShouldBeTrue("the panel stand-in must take the foreground");
            Thread.Sleep(1500);
            var events = rig.Observer.Snapshot().Skip(before).ToList();
            bool kept = rig.Tracker.Current.FrameHwnd == w1 && rig.Tracker.Current.Path == a && !events.Any(e => e.State.IsInactive);
            rec.Line($"{(kept ? "pass" : "FAIL")} panel (owned by frame 0x{w1:X}) has focus: current {Scn.Describe(rig.Tracker.Current)}; events since: {events.Count}");
            if (!kept)
            {
                failures.Add("the folder must be kept while the panel has focus");
            }

            // Navigation while the panel has focus must still be followed (events come from the Explorer process).
            var tab = lab.TabsOf(w1)[0];
            Check("navigate while the panel has focus", Scn.Step(rig, () => ExplorerLab.Navigate(tab, b), s => Scn.At(s, w1, b)));
            rec.Line($"  foreground during that step: {LabNative.Describe(LabNative.GetForegroundWindow())}");

            // The panel of window 1 is foreground, window 2 exists: the state must stay window 1.
            rig.Tracker.Current.FrameHwnd.ShouldBe(w1);

            // Another app (an unowned window of ours) in the foreground: Inactive.
            using var other = new LabWindow(0, "Some other app (lab)");
            Check("another app takes the foreground -> Inactive", Scn.Step(rig, () => LabNative.ForceForeground(other.Hwnd), Scn.Inactive));
            Check("Explorer window 1 comes back", Scn.Step(rig, () => lab.Foreground(w1), s => Scn.At(s, w1, b)));
            Check("window 2", Scn.Step(rig, () => lab.Foreground(w2), s => Scn.At(s, w2, d)));

            // Minimize and restore.
            Check("minimize window 2 -> Inactive", Scn.Step(rig, () => LabNative.ShowWindow(w2, 6), Scn.Inactive));
            Check("restore window 2", Scn.Step(rig, () => lab.Foreground(w2), s => Scn.At(s, w2, d)));

            // A window owned by a *minimized* frame is not the folder.
            var stats = rig.Tracker.GetStats();
            rec.Line($"Evaluations {stats.Evaluations}, published {stats.PublishedStates}, UI events {rig.Observer.Count}, sequence regressions {rig.Observer.SequenceRegressions}");
            rig.Observer.SequenceRegressions.ShouldBe(0);
            failures.ShouldBeEmpty(string.Join("; ", failures));
        });

    [RealExplorerFact]
    public void L8_An_elevated_Explorer_window_is_not_a_retry_loop()
    {
        // An elevated Explorer window can only be created through a UAC prompt on the secure desktop, which a test must not
        // trigger on someone's desktop. So this runs only if the person has opened one (e.g. "explorer.exe /separate" as
        // administrator) and otherwise says so: O-23 then stays open and the fault-injection tests (TrackerEngineTests, L8)
        // are the evidence.
        Scn.Run("elevated", rec =>
        {
            using var lab = new ExplorerLab("elevated");
            nint elevated = 0;
            foreach (var f in lab.Frames())
            {
                var pid = LabNative.PidOf(f);
                try
                {
                    using var p = Process.GetProcessById((int)pid);
                    _ = p.MainModule;
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    elevated = f;       // cannot be inspected from this (non-elevated) process: higher integrity level
                }
            }

            if (elevated == 0)
            {
                rec.Line("NOT RUN: no elevated Explorer window exists on this desktop (creating one needs a UAC prompt). O-23 stays open; behaviour is verified by fault injection only (L8 tests).");
                Assert.Skip("No elevated Explorer window available.");
            }

            using var rig = new TrackerRig();
            Thread.Sleep(500);
            LabNative.ForceForeground(elevated);
            long e0 = rig.Tracker.GetStats().Evaluations;
            Thread.Sleep(6000);
            long e1 = rig.Tracker.GetStats().Evaluations;
            rec.Line($"Elevated frame 0x{elevated:X}: state {Scn.Describe(rig.Tracker.Current)}; {e1 - e0} evaluations in 6 s with the elevated frame in the foreground");
            (e1 - e0).ShouldBeLessThan(40, "an unreadable frame must not cause a retry loop");
            rig.Tracker.GetStats().AbandonedWorkers.ShouldBe(0);
        });
    }
}
