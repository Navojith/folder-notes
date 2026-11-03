using System.Diagnostics;
using FolderNotes.App.Explorer;

namespace FolderNotes.App.Tests.Explorer.Real;

/// <summary>
/// T3.2.5 measurements against the real Explorer: REQ-031 latency, evaluation cost, idle CPU (NFR-06), lost events
/// (04 D.7a) and hook scoping. All TIMING-BASED: they measure, and assert only the budgets the specs state.
/// </summary>
[Collection("RealExplorer")]
[Trait("Category", "Explorer")]
[Trait("Kind", "RealExplorer")]
public sealed class RealExplorerPerformanceTests
{
    [RealExplorerFact]
    public void REQ031_Latency_and_evaluation_cost_on_the_real_Explorer() =>
        Scn.Run("latency", rec =>
        {
            using var lab = new ExplorerLab("latency");
            using var rig = new TrackerRig();
            string[] dirs = [lab.Mk("L1"), lab.Mk("L2"), lab.Mk("L3"), lab.Mk("L4")];
            string other = lab.Mk("LW");
            var failures = new List<string>();
            var nav = new List<double>();
            var tabs = new List<double>();
            var wins = new List<double>();

            nint w1 = lab.OpenWindow(dirs[0]);
            lab.Foreground(w1).ShouldBeTrue();
            rig.Observer.Await(s => Scn.At(s, w1, dirs[0]), 0).Ms.ShouldNotBeNull();
            nint w2 = lab.OpenWindow(other);
            lab.Foreground(w1).ShouldBeTrue();
            rig.Observer.Await(s => Scn.At(s, w1, dirs[0]), 0).Ms.ShouldNotBeNull();
            rig.Ui.ResetResponsiveness();

            // navigation: 40 steps through four folders
            for (int i = 1; i <= 40; i++)
            {
                var target = dirs[i % 4];
                var tab = lab.TabsOf(w1)[0];
                var r = Scn.Step(rig, () => ExplorerLab.Navigate(tab, target), s => Scn.At(s, w1, target), settleMs: 100);
                if (r.Ms is null)
                {
                    failures.Add($"navigate #{i}: {Scn.Describe(r.Seen)}");
                }
                else
                {
                    nav.Add(r.Ms.Value);
                }
            }

            // tab switching: two tabs on different folders, 40 Ctrl+Tab
            var t2 = lab.NewTab(w1);
            string tabFolder = lab.Mk("LT");
            Scn.Step(rig, () => ExplorerLab.Navigate(t2, tabFolder), s => Scn.At(s, w1, tabFolder)).Ms.ShouldNotBeNull();
            string[] pair = [dirs[0], tabFolder];
            int active = 1;
            string firstFolder = lab.TabsOf(w1).Select(ExplorerLab.OraclePath).First(p => !string.Equals(p, tabFolder, StringComparison.OrdinalIgnoreCase))!;
            pair[0] = firstFolder;
            for (int i = 1; i <= 40; i++)
            {
                active = 1 - active;
                var target = pair[active];
                var r = Scn.Step(rig, () => lab.Keys(w1, LabNative.VK_CONTROL, LabNative.VK_TAB), s => Scn.At(s, w1, target), settleMs: 100);
                if (r.Ms is null)
                {
                    failures.Add($"tab switch #{i}: {Scn.Describe(r.Seen)}");
                }
                else
                {
                    tabs.Add(r.Ms.Value);
                }
            }

            // window switching
            string w1Path = rig.Observer.Last.Path!;
            for (int i = 1; i <= 30; i++)
            {
                var r = Scn.Step(rig, () => lab.Foreground(w2), s => Scn.At(s, w2, other), settleMs: 100);
                if (r.Ms is null)
                {
                    failures.Add($"window switch to 2 #{i}: {Scn.Describe(r.Seen)}");
                }
                else
                {
                    wins.Add(r.Ms.Value);
                }

                var r1 = Scn.Step(rig, () => lab.Foreground(w1), s => Scn.At(s, w1, w1Path), settleMs: 100);
                if (r1.Ms is null)
                {
                    failures.Add($"window switch to 1 #{i}: {Scn.Describe(r1.Seen)}");
                }
                else
                {
                    wins.Add(r1.Ms.Value);
                }
            }

            var stats = rig.Tracker.GetStats();
            var ui = rig.Ui.Responsiveness();
            rec.Line($"Navigate (action -> UI handler): {Stat.Summary(nav)}");
            rec.Line($"Tab switch (Ctrl+Tab -> UI handler): {Stat.Summary(tabs)}");
            rec.Line($"Window switch (foreground -> UI handler): {Stat.Summary(wins)}");
            rec.Line($"Evaluation cost, TrackerStats (last 256 of {stats.Evaluations}): p50 {stats.EvaluationP50Ms:F2} ms, p95 {stats.EvaluationP95Ms:F2} ms, max {stats.EvaluationMaxMs:F2} ms; rebuilds {stats.Rebuilds}; published {stats.PublishedStates}; UI events {rig.Observer.Count}");
            rec.Line($"UI heartbeat (5 ms) lateness over {ui.Count} beats: p50 {ui.P50:F2} ms, p99 {ui.P99:F2} ms, max {ui.Max:F2} ms");
            rec.Line($"Sequence regressions {rig.Observer.SequenceRegressions}; abandoned workers {stats.AbandonedWorkers}; live RCWs {stats.LiveRcws}");

            failures.ShouldBeEmpty(string.Join(Environment.NewLine, failures));
            rig.Observer.SequenceRegressions.ShouldBe(0);
            stats.AbandonedWorkers.ShouldBe(0);
            stats.EvaluationP50Ms.ShouldBeLessThan(10, "REQ-031: the typical evaluation costs under 10 ms (the tail is reported, see the T3.2.5 report)");
            Stat.Percentile(nav, 95).ShouldBeLessThanOrEqualTo(150, "REQ-031: navigation is followed within 150 ms (p95)");
            Stat.Percentile(tabs, 95).ShouldBeLessThanOrEqualTo(150, "REQ-031: tab switches are followed within 150 ms (p95)");
            Stat.Percentile(wins, 95).ShouldBeLessThanOrEqualTo(150, "REQ-031: window switches are followed within 150 ms (p95)");
        });

    [RealExplorerFact]
    public void NFR06_Idle_CPU_with_Explorer_in_the_foreground_and_in_the_background() =>
        Scn.Run("idle-cpu", rec =>
        {
            using var lab = new ExplorerLab("idle");
            string a = lab.Mk("IdleA");
            nint inShell = lab.OpenWindow(a);                       // a lab window (it has its own process too; see the report)
            nint own = lab.OpenWindow(lab.Mk("IdleSep"), separateProcess: true);
            using var other = new LabWindow(0, "Some other app (lab)");
            const int Seconds = 60;

            // The CPU of this whole test process over 60 s, minus the same process without the tracker (the host's own noise).
            double Measure(Action? during = null)
            {
                Thread.Sleep(500);
                double c0 = LabNative.ProcessCycleCpuMs();
                var sw = Stopwatch.StartNew();
                during?.Invoke();
                Thread.Sleep(Math.Max(0, (Seconds * 1000) - (int)sw.ElapsedMilliseconds));
                return LabNative.ProcessCycleCpuMs() - c0;
            }

            LabNative.ForceForeground(inShell).ShouldBeTrue();
            double baseline = Measure();
            rec.Line($"Baseline, no tracker, {Seconds} s: {baseline:F1} ms CPU ({baseline / (Seconds * 10.0):F3} % of one core)");

            var rows = new List<(string Name, double Cpu, long Evals, long Callbacks, long Published, int Hooks)>();

            void Run(string name, Action setup, bool heartbeat = false)
            {
                using var winEvents = new TrackerRig(heartbeat: heartbeat, startNow: false);
                setup();
                winEvents.Tracker.Start().ShouldBe(StartResult.Started);
                Thread.Sleep(2500);                                      // let the first evaluations and the scope settle
                var s0 = winEvents.Tracker.GetStats();
                long cb0 = winEvents.WinEvents.Callbacks;
                double cpu = Measure();
                var s1 = winEvents.Tracker.GetStats();
                rows.Add((name, cpu, s1.Evaluations - s0.Evaluations, winEvents.WinEvents.Callbacks - cb0, s1.PublishedStates - s0.PublishedStates, winEvents.WinEvents.LiveHooks));
                rec.Line($"{name}: {cpu:F1} ms CPU in {Seconds} s = {cpu / (Seconds * 10.0):F3} % of one core (minus baseline {Math.Max(0, cpu - baseline) / (Seconds * 10.0):F3} %); evaluations {s1.Evaluations - s0.Evaluations}, hook callbacks {winEvents.WinEvents.Callbacks - cb0}, published {s1.PublishedStates - s0.PublishedStates}, live hooks {winEvents.WinEvents.LiveHooks}");
                winEvents.Tracker.Stop();
            }

            Run("Explorer foreground, untouched, window A (run 1)", () => LabNative.ForceForeground(inShell).ShouldBeTrue());
            Run("Explorer foreground, untouched, window B", () => LabNative.ForceForeground(own).ShouldBeTrue());
            Run("Another app foreground", () => LabNative.ForceForeground(other.Hwnd).ShouldBeTrue());
            Run("Explorer foreground, untouched, window A (run 2, repeat)", () => LabNative.ForceForeground(inShell).ShouldBeTrue());

            // Pass criteria (06): < 0.1 % of one core (60 ms in 60 s) with Explorer foreground; 0 evaluations and 0 callbacks with another app.
            var fgShell = rows[0];
            var fgOwn = rows[1];
            var bg = rows[2];
            var fgRepeat = rows[3];
            rec.Line($"Verdict inputs: foreground(A) {fgShell.Cpu:F1} ms, foreground(B) {fgOwn.Cpu:F1} ms, background {bg.Cpu:F1} ms, baseline {baseline:F1} ms (limit 60 ms; from the process cycle counter, not the 15.6 ms clock tick)");
            bg.Evals.ShouldBe(0, "no evaluation while another app is foreground");
            bg.Callbacks.ShouldBe(0, "no hook callback while another app is foreground");
            bg.Hooks.ShouldBeLessThanOrEqualTo(1, "only the foreground hook stays installed while another app is foreground");
            fgOwn.Cpu.ShouldBeLessThan(60, "NFR-06: < 0.1 % of one core with Explorer foreground");
            fgShell.Cpu.ShouldBeLessThan(60, "NFR-06: < 0.1 % of one core with Explorer foreground");
            fgRepeat.Cpu.ShouldBeLessThan(60, "NFR-06: < 0.1 % of one core with Explorer foreground");
        });

    [RealExplorerFact]
    public void D2_Lost_events_converge_through_the_poll_within_2_5_seconds() =>
        Scn.Run("lost-events", rec =>
        {
            using var lab = new ExplorerLab("lost");
            string[] dirs = [lab.Mk("E1"), lab.Mk("E2"), lab.Mk("E3")];
            string other = lab.Mk("EW");
            var failures = new List<string>();

            // Every event lost: the hooks "install" but nothing is ever delivered.
            using (var rig = new TrackerRig(installNothing: true, startNow: false))
            {
                nint w1 = lab.OpenWindow(dirs[0]);
                nint w2 = lab.OpenWindow(other);
                lab.Foreground(w1).ShouldBeTrue();
                rig.Tracker.Start().ShouldBe(StartResult.Started);
                rig.Observer.Await(s => Scn.At(s, w1, dirs[0]), 0, 4000).Ms.ShouldNotBeNull();

                var fast = new List<double>();
                var slow = new List<double>();
                void Nav(int i, List<double> into, string phase)
                {
                    var target = dirs[i % 3];
                    var tab = lab.TabsOf(w1)[0];
                    var r = Scn.Step(rig, () => ExplorerLab.Navigate(tab, target), s => Scn.At(s, w1, target), settleMs: 50, timeoutMs: 6000);
                    if (r.Ms is null)
                    {
                        failures.Add($"{phase} #{i}: no convergence ({Scn.Describe(r.Seen)})");
                    }
                    else
                    {
                        into.Add(r.Ms.Value);
                    }
                }

                for (int i = 1; i <= 10; i++)
                {
                    Nav(i, fast, "all events lost, poll active");
                }

                Thread.Sleep(12_000);                                  // 10 s of silence: the poll backs off to 2 s
                for (int i = 11; i <= 20; i++)
                {
                    Nav(i, slow, "all events lost, poll backed off");
                    Thread.Sleep(11_000);                              // back to silence before the next one
                }

                // Foreground changes without the foreground event
                var fg = new List<double>();
                for (int i = 0; i < 6; i++)
                {
                    var r = Scn.Step(rig, () => lab.Foreground(w2), s => Scn.At(s, w2, other), settleMs: 50, timeoutMs: 6000);
                    var r1 = Scn.Step(rig, () => lab.Foreground(w1), s => s.FrameHwnd == w1, settleMs: 50, timeoutMs: 6000);
                    if (r.Ms is null || r1.Ms is null)
                    {
                        failures.Add($"foreground switch #{i} without events: {Scn.Describe(r.Seen)} / {Scn.Describe(r1.Seen)}");
                    }
                    else
                    {
                        fg.Add(r.Ms.Value);
                        fg.Add(r1.Ms.Value);
                    }
                }

                // A tab created and a tab closed with no events (the map is repaired by a miss and by validation)
                var newTab = lab.NewTab(w1);
                string tabDir = lab.Mk("ETab");
                var created = Scn.Step(rig, () => ExplorerLab.Navigate(newTab, tabDir), s => Scn.At(s, w1, tabDir), settleMs: 50, timeoutMs: 6000);
                rec.Line($"New tab navigated with all events lost: {(created.Ms is null ? "FAIL " + Scn.Describe(created.Seen) : created.Ms.Value.ToString("F0") + " ms")}");
                if (created.Ms is null)
                {
                    failures.Add("new tab without events");
                }

                var stats = rig.Tracker.GetStats();
                rec.Line($"ALL EVENTS LOST (poll only): convergence while the poll is fast (<10 s of silence): {Stat.Summary(fast)}");
                rec.Line($"ALL EVENTS LOST: convergence after 10 s+ of silence (poll at 2 s): {Stat.Summary(slow)}");
                rec.Line($"ALL EVENTS LOST: foreground switches: {Stat.Summary(fg)}");
                rec.Line($"Evaluations {stats.Evaluations}, rebuilds {stats.Rebuilds}, hooks active {stats.HooksActive}, hook callbacks {rig.WinEvents.Callbacks}");
                Stat.Percentile(slow, 100).ShouldBeLessThanOrEqualTo(2500, "04 D.7a: stale for at most about 2 s without any event (plus the evaluation)");
                Stat.Percentile(fast, 100).ShouldBeLessThanOrEqualTo(2500);
                rig.Tracker.Stop();
            }

            // Half of the events lost at random (deterministic seed)
            var rnd = new Random(325);
            using (var rig = new TrackerRig(dropEvents: _ => rnd.Next(2) == 0, startNow: false))
            {
                nint w1 = lab.OpenWindow(dirs[1]);
                lab.Foreground(w1).ShouldBeTrue();
                rig.Tracker.Start().ShouldBe(StartResult.Started);
                rig.Observer.Await(s => Scn.At(s, w1, dirs[1]), 0, 4000).Ms.ShouldNotBeNull();
                var lat = new List<double>();
                for (int i = 1; i <= 30; i++)
                {
                    var target = dirs[(i + 1) % 3];
                    var tab = lab.TabsOf(w1)[0];
                    var r = Scn.Step(rig, () => ExplorerLab.Navigate(tab, target), s => Scn.At(s, w1, target), settleMs: 100, timeoutMs: 6000);
                    if (r.Ms is null)
                    {
                        failures.Add($"50% loss #{i}: {Scn.Describe(r.Seen)}");
                    }
                    else
                    {
                        lat.Add(r.Ms.Value);
                    }
                }

                rec.Line($"50% OF EVENTS LOST: navigate -> UI handler: {Stat.Summary(lat)}; callbacks {rig.WinEvents.Callbacks}, dropped {rig.WinEvents.Dropped}");
                Stat.Percentile(lat, 100).ShouldBeLessThanOrEqualTo(2500);
                rig.Tracker.Stop();
            }

            failures.ShouldBeEmpty(string.Join(Environment.NewLine, failures));
        });

    [RealExplorerFact]
    public void NFR06_Identical_hook_scopes_do_not_cause_reinstallation() =>
        Scn.Run("hook-scope", rec =>
        {
            using var lab = new ExplorerLab("scope");
            using var rig = new TrackerRig(startNow: false);
            string a = lab.Mk("SA"), b = lab.Mk("SB"), c = lab.Mk("SC"), sep = lab.Mk("SSep");
            nint w1 = lab.OpenWindow(a);
            nint w1b = lab.NewWindowFromFrame(w1);             // Ctrl+N: another window of the same process
            nint w3 = lab.OpenWindow(sep, separateProcess: true);
            uint pidShell = LabNative.PidOf(w1);          // the process of window 1 (a lab process, not the shell: plain explorer.exe windows get their own)
            uint pidOwn = LabNative.PidOf(w3);
            pidOwn.ShouldNotBe(pidShell);
            LabNative.PidOf(w1b).ShouldBe(pidShell);

            lab.Foreground(w1).ShouldBeTrue();
            rig.Tracker.Start().ShouldBe(StartResult.Started);
            rig.Observer.Await(s => s.FrameHwnd == w1, 0).Ms.ShouldNotBeNull();
            Thread.Sleep(500);
            int afterStart = rig.WinEvents.Installs;
            int scopedPerScope = afterStart - 1;                // one global foreground hook + the scoped ones
            rec.Line($"After start (foreground in the shell process): {afterStart} installs ({scopedPerScope} scoped + 1 foreground), live {rig.WinEvents.LiveHooks}");

            // 1. thirty navigations inside one window
            for (int i = 0; i < 30; i++)
            {
                var target = i % 2 == 0 ? b : c;
                var tab = lab.TabsOf(w1)[0];
                Scn.Step(rig, () => ExplorerLab.Navigate(tab, target), s => Scn.At(s, w1, target), settleMs: 60).Ms.ShouldNotBeNull();
            }

            rec.Line($"After 30 navigations in one window: installs {rig.WinEvents.Installs} (expected {afterStart}), unhooks {rig.WinEvents.Unhooks}");
            rig.WinEvents.Installs.ShouldBe(afterStart);

            // 2. twenty switches between two windows of the same process
            string w1Path = rig.Observer.Last.Path!;
            for (int i = 0; i < 10; i++)
            {
                Scn.Step(rig, () => lab.Foreground(w1b), s => s.FrameHwnd == w1b, settleMs: 60).Ms.ShouldNotBeNull();
                Scn.Step(rig, () => lab.Foreground(w1), s => Scn.At(s, w1, w1Path), settleMs: 60).Ms.ShouldNotBeNull();
            }

            rec.Line($"After 20 same-process window switches: installs {rig.WinEvents.Installs} (expected {afterStart}), unhooks {rig.WinEvents.Unhooks}");
            rig.WinEvents.Installs.ShouldBe(afterStart);

            // 3. switches between the processes: each change of process re-scopes once
            int before = rig.WinEvents.Installs;
            for (int i = 0; i < 5; i++)
            {
                Scn.Step(rig, () => lab.Foreground(w3), s => Scn.At(s, w3, sep), settleMs: 60).Ms.ShouldNotBeNull();
                Scn.Step(rig, () => lab.Foreground(w1), s => Scn.At(s, w1, w1Path), settleMs: 60).Ms.ShouldNotBeNull();
            }

            int delta = rig.WinEvents.Installs - before;
            rec.Line($"After 10 cross-process switches: {delta} installs = {delta / (double)Math.Max(1, scopedPerScope):F1} scope changes of {scopedPerScope} hooks (expected 10), live hooks {rig.WinEvents.LiveHooks}; scopes requested: {string.Join(",", rig.WinEvents.InstalledScopes.Distinct())}");
            delta.ShouldBe(10 * scopedPerScope, "each change of process installs the scoped hooks once, nothing more");

            // 4. another app foreground removes the scoped hooks
            using var otherApp = new LabWindow(0, "Some other app (lab)");
            Scn.Step(rig, () => LabNative.ForceForeground(otherApp.Hwnd), Scn.Inactive).Ms.ShouldNotBeNull();
            Thread.Sleep(300);
            rec.Line($"Another app in the foreground: live hooks {rig.WinEvents.LiveHooks}");
            rig.WinEvents.LiveHooks.ShouldBe(1);

            rig.Tracker.Stop();
            rec.Line($"After Stop: live hooks {rig.WinEvents.LiveHooks}, installs {rig.WinEvents.Installs}, unhooks {rig.WinEvents.Unhooks}");
            rig.WinEvents.LiveHooks.ShouldBe(0);
            rig.Observer.SequenceRegressions.ShouldBe(0);
        });
}
