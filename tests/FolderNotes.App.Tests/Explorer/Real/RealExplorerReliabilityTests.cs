using System.Diagnostics;
using FolderNotes.App.Explorer;

namespace FolderNotes.App.Tests.Explorer.Real;

/// <summary>
/// T3.2.5 reliability checks on the real Explorer: churn, bounded Stop, and a hung Explorer process that the lab itself
/// started (never the user's shell). TIMING-BASED, like the other real-Explorer tests.
/// </summary>
[Collection("RealExplorer")]
[Trait("Category", "Explorer")]
[Trait("Kind", "RealExplorer")]
public sealed class RealExplorerReliabilityTests
{
    private static (long Private, uint Handles, int Threads) Footprint()
    {
        for (int i = 0; i < 2; i++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
        }

        using var p = Process.GetCurrentProcess();
        return (p.PrivateMemorySize64, LabNative.HandleCount(), p.Threads.Count);
    }

    [RealExplorerFact]
    public void REQ031_Rapid_navigation_ends_on_the_folder_Explorer_shows() =>
        Scn.Run("rapid-navigation", rec =>
        {
            using var lab = new ExplorerLab("rapidnav");
            using var rig = new TrackerRig();
            string[] dirs = [lab.Mk("R1"), lab.Mk("R2"), lab.Mk("R3"), lab.Mk(@"R4\r4a\r4b\r4c"), lab.Mk("R5")];
            nint w1 = lab.OpenWindow(dirs[0]);
            lab.Foreground(w1).ShouldBeTrue();
            rig.Observer.Await(s => Scn.At(s, w1, dirs[0]), 0).Ms.ShouldNotBeNull();
            rig.Ui.ResetResponsiveness();
            var tab = lab.TabsOf(w1)[0];
            var s0 = rig.Tracker.GetStats();
            int ev0 = rig.Observer.Count;
            var sw = Stopwatch.StartNew();
            const int Steps = 200;
            for (int i = 0; i < Steps; i++)
            {
                if (i % 4 == 3)
                {
                    lab.Keys(w1, LabNative.VK_MENU, LabNative.VK_UP);          // Alt+Up: parent folder
                }
                else
                {
                    ExplorerLab.Navigate(tab, dirs[i % 5]);
                }

                Thread.Sleep(10);
            }

            double drive = sw.Elapsed.TotalMilliseconds;
            double titleSettled = Scn.SettleTitle(w1);                        // Explorer keeps working through queued keys
            Thread.Sleep(1500);
            var oracle = ExplorerLab.OraclePath(lab.TabsOf(w1)[0]);
            var published = rig.Tracker.Current;
            var s1 = rig.Tracker.GetStats();
            int uiEvents = rig.Observer.Count - ev0;
            long evals = s1.Evaluations - s0.Evaluations;
            long pubs = s1.PublishedStates - s0.PublishedStates;
            var ui = rig.Ui.Responsiveness();
            rec.Line($"{Steps} steps (150 Navigate2 + 50 Alt+Up) in {drive:F0} ms; Explorer's title stopped changing {titleSettled:F0} ms after the first key (+1.5 s before comparing): Explorer's tab path '{oracle}', frame title '{LabNative.Title(w1)}', published '{published.Path}'");
            rec.Line($"Evaluations {evals} ({evals * 30.0 / drive:F2} of the 1-per-30-ms ceiling), published {pubs}, delivered to the UI thread {uiEvents} (latest-wins merged {pubs - uiEvents}), rebuilds {s1.Rebuilds - s0.Rebuilds}; evaluation p95 {s1.EvaluationP95Ms:F2} ms, max {s1.EvaluationMaxMs:F2} ms");
            rec.Line($"UI heartbeat during the churn: {ui.Count} beats, p50 {ui.P50:F2} ms, p99 {ui.P99:F2} ms, max {ui.Max:F2} ms; sequence regressions {rig.Observer.SequenceRegressions}");
            published.FrameHwnd.ShouldBe(w1);
            string.Equals(published.Path, oracle, StringComparison.OrdinalIgnoreCase).ShouldBeTrue("the final published state must equal Explorer's");
            Scn.TitleShows(w1, published.Path!).ShouldBeTrue($"the published folder {published.Path} must be the one Explorer shows: '{LabNative.Title(w1)}'");
            pubs.ShouldBeLessThanOrEqualTo(Steps + 10, "published events are bounded by the number of steps plus corrections");
            evals.ShouldBeLessThanOrEqualTo((long)((drive / 30.0 * 1.5) + 40), "about one evaluation per 30 ms, never a burst");
            ui.P99.ShouldBeLessThan(50, "the UI thread stays responsive during the churn");
            rig.Observer.SequenceRegressions.ShouldBe(0);
            s1.AbandonedWorkers.ShouldBe(0);
        });

    [RealExplorerFact]
    public void REQ034_Rapid_tab_switching_ends_on_the_tab_Explorer_shows() =>
        Scn.Run("rapid-tabs", rec =>
        {
            using var lab = new ExplorerLab("rapidtabs");
            using var rig = new TrackerRig();
            string a = lab.Mk("TabsA"), b = lab.Mk("TabsB"), c = lab.Mk("TabsC");
            nint w1 = lab.OpenWindow(a);
            lab.Foreground(w1).ShouldBeTrue();
            var t2 = lab.NewTab(w1);
            Scn.Step(rig, () => ExplorerLab.Navigate(t2, b), s => Scn.At(s, w1, b)).Ms.ShouldNotBeNull();
            var t3 = lab.NewTab(w1);
            Scn.Step(rig, () => ExplorerLab.Navigate(t3, c), s => Scn.At(s, w1, c)).Ms.ShouldNotBeNull();
            rig.Ui.ResetResponsiveness();
            var s0 = rig.Tracker.GetStats();
            int ev0 = rig.Observer.Count;
            var sw = Stopwatch.StartNew();
            const int Steps = 200;
            for (int i = 0; i < Steps; i++)
            {
                lab.Keys(w1, LabNative.VK_CONTROL, LabNative.VK_TAB);
                Thread.Sleep(15);
            }

            double drive = sw.Elapsed.TotalMilliseconds;
            double titleSettled = Scn.SettleTitle(w1);
            Thread.Sleep(1500);
            var published = rig.Tracker.Current;
            var s1 = rig.Tracker.GetStats();
            long evals = s1.Evaluations - s0.Evaluations;
            long pubs = s1.PublishedStates - s0.PublishedStates;
            int uiEvents = rig.Observer.Count - ev0;
            var ui = rig.Ui.Responsiveness();
            rec.Line($"{Steps} Ctrl+Tab in {drive:F0} ms; Explorer's title stopped changing {titleSettled:F0} ms after the first key (+1.5 s before comparing): Explorer's frame title '{LabNative.Title(w1)}', published '{published.Path}'");
            rec.Line($"Evaluations {evals}, published {pubs}, delivered {uiEvents}, rebuilds {s1.Rebuilds - s0.Rebuilds}; UI heartbeat p99 {ui.P99:F2} ms, max {ui.Max:F2} ms");
            published.FrameHwnd.ShouldBe(w1);
            Scn.TitleShows(w1, published.Path!).ShouldBeTrue($"the published folder {published.Path} must be the tab Explorer shows: '{LabNative.Title(w1)}'");
            pubs.ShouldBeLessThanOrEqualTo(Steps + 10);
            evals.ShouldBeLessThanOrEqualTo((long)((drive / 30.0 * 1.5) + 40));
            ui.P99.ShouldBeLessThan(50);
            rig.Observer.SequenceRegressions.ShouldBe(0);
            s1.AbandonedWorkers.ShouldBe(0);
        });

    [RealExplorerFact]
    public void NFR01_Opening_and_closing_50_windows_and_50_tabs_leaks_nothing() =>
        Scn.Run("churn", rec =>
        {
            using var lab = new ExplorerLab("churn");
            using var rig = new TrackerRig();
            string baseDir = lab.Mk("ChurnBase");
            nint w0 = lab.OpenWindow(baseDir);
            lab.Foreground(w0).ShouldBeTrue();
            rig.Observer.Await(s => Scn.At(s, w0, baseDir), 0).Ms.ShouldNotBeNull();

            // warm-up cycle so one-time allocations are not counted
            nint warm = lab.OpenWindow(lab.Mk("Warm"));
            lab.Foreground(warm);
            Thread.Sleep(400);
            lab.Close(warm);
            lab.Foreground(w0);
            Thread.Sleep(1500);
            var rundllBefore = Process.GetProcessesByName("rundll32").Select(p => p.Id).ToHashSet();
            var explorerBefore = Process.GetProcessesByName("explorer").Select(p => p.Id).ToHashSet();
            var fp0 = Footprint();
            var rcw0 = rig.Tracker.GetStats().LiveRcws;
            rig.Ui.ResetResponsiveness();
            var failures = new List<string>();
            var openLat = new List<double>();

            for (int i = 0; i < 50; i++)
            {
                string dir = lab.Mk($"Churn\\W{i}");
                long ts = Stopwatch.GetTimestamp();
                nint w = lab.OpenWindow(dir);
                lab.Foreground(w);
                var r = rig.Observer.Await(s => Scn.At(s, w, dir), ts, 4000);
                if (r.Ms is null)
                {
                    failures.Add($"window {i}: {Scn.Describe(r.Seen)}");
                }
                else
                {
                    openLat.Add(r.Ms.Value);
                }

                lab.Close(w);
            }

            lab.Foreground(w0);
            // Explorer stops accepting Ctrl+T at 46 tabs on this build (seen when 50 were tried in one go), so: 2 rounds of 25.
            for (int round = 0; round < 2; round++)
            {
                for (int i = 0; i < 25; i++)
                {
                    var t = lab.NewTab(w0);
                    string dir = lab.Mk(Path.Combine("ChurnTabs", $"T{round}_{i}"));
                    var r = Scn.Step(rig, () => ExplorerLab.Navigate(t, dir), s => Scn.At(s, w0, dir), settleMs: 30);
                    if (r.Ms is null)
                    {
                        failures.Add($"tab {round}/{i}: {Scn.Describe(r.Seen)}");
                    }
                }

                for (int i = 0; i < 25; i++)
                {
                    lab.Keys(w0, LabNative.VK_CONTROL, LabNative.VK_W);
                    Thread.Sleep(80);
                }
            }

            Thread.Sleep(3000);
            var settled = Scn.Step(rig, () => lab.Foreground(w0), s => s.FrameHwnd == w0, settleMs: 500);
            var fp1 = Footprint();
            var stats = rig.Tracker.GetStats();
            var ui = rig.Ui.Responsiveness();
            var rundllAfter = Process.GetProcessesByName("rundll32").Select(p => p.Id).Where(id => !rundllBefore.Contains(id)).ToList();
            var explorerAfter = Process.GetProcessesByName("explorer").Select(p => p.Id).Where(id => !explorerBefore.Contains(id)).ToList();
            rec.Line($"50 windows opened and closed: open -> published {Stat.Summary(openLat)}");
            rec.Line($"50 tabs opened and closed in one window; final state {Scn.Describe(rig.Tracker.Current)} (settled: {(settled.Ms is null ? "no" : "yes")}); Explorer's frame title '{LabNative.Title(w0)}'");
            rec.Line($"Private bytes {fp0.Private / 1048576.0:F2} -> {fp1.Private / 1048576.0:F2} MB (delta {(fp1.Private - fp0.Private) / 1048576.0:F2} MB); handles {fp0.Handles} -> {fp1.Handles}; threads {fp0.Threads} -> {fp1.Threads}; live RCWs {rcw0} -> {stats.LiveRcws} (cross-thread releases {stats.CrossThreadReleases})");
            rec.Line($"Evaluations {stats.Evaluations}, rebuilds {stats.Rebuilds}, published {stats.PublishedStates}, abandoned {stats.AbandonedWorkers}, UI heartbeat p99 {ui.P99:F2} ms, max {ui.Max:F2} ms");
            rec.Line($"New rundll32 processes: {rundllAfter.Count}; new explorer processes: {explorerAfter.Count}");
            failures.ShouldBeEmpty(string.Join(Environment.NewLine, failures));
            (fp1.Private - fp0.Private).ShouldBeLessThanOrEqualTo(1_048_576L, "private bytes grow at most 1 MB over the loop");
            ((long)fp1.Handles - fp0.Handles).ShouldBeLessThanOrEqualTo(10, "handles grow at most 10 over the loop");
            stats.LiveRcws.ShouldBeLessThanOrEqualTo(rcw0 + 4, "the live RCWs return to the baseline (a few for the open tabs)");
            rundllAfter.ShouldBeEmpty("we must not start a rundll32 server");
            explorerAfter.ShouldBeEmpty("we must not start an explorer process");
            rig.Observer.SequenceRegressions.ShouldBe(0);
            stats.AbandonedWorkers.ShouldBe(0);
            ui.P99.ShouldBeLessThan(50);
        });

    [RealExplorerFact]
    public void NFR01_Stop_is_bounded_and_start_stop_cycles_release_everything() =>
        Scn.Run("start-stop", rec =>
        {
            using var lab = new ExplorerLab("startstop");
            using var rig = new TrackerRig(startNow: false);
            string a = lab.Mk("SS");
            nint w1 = lab.OpenWindow(a);
            lab.Foreground(w1).ShouldBeTrue();
            var stopMs = new List<double>();
            var startMs = new List<double>();
            var fp0 = Footprint();
            for (int i = 0; i < 20; i++)
            {
                long ts = Stopwatch.GetTimestamp();
                rig.Tracker.Start().ShouldBe(StartResult.Started);
                var first = rig.Observer.Await(s => Scn.At(s, w1, a), ts, 4000);
                first.Ms.ShouldNotBeNull($"cycle {i}: first state after Start");
                startMs.Add(first.Ms!.Value);
                if (i % 2 == 1)
                {
                    // Stop in the middle of activity
                    var tab = lab.TabsOf(w1)[0];
                    for (int k = 0; k < 10; k++)
                    {
                        ExplorerLab.Navigate(tab, k % 2 == 0 ? lab.Mk("SS2") : a);
                    }
                }

                var sw = Stopwatch.StartNew();
                rig.Tracker.Stop();
                stopMs.Add(sw.Elapsed.TotalMilliseconds);
                var st = rig.Tracker.GetStats();
                st.LiveRcws.ShouldBe(0, $"cycle {i}: live RCWs after Stop");
                rig.WinEvents.LiveHooks.ShouldBe(0, $"cycle {i}: live hooks after Stop");
                rig.Observer.Await(s => s.IsInactive, 0, 1000).Ms.ShouldNotBeNull($"cycle {i}: Inactive after Stop");
                rig.Observer.Last.IsInactive.ShouldBeTrue($"cycle {i}: last state after Stop");
                Thread.Sleep(100);
            }

            var fp1 = Footprint();
            rec.Line($"20 Start/Stop cycles on the real Explorer. Start -> first state: {Stat.Summary(startMs)}");
            rec.Line($"Stop duration: {Stat.Summary(stopMs)} (bound: 2 s worker + 1 s hooks + 1 s watchdog)");
            rec.Line($"Private bytes {fp0.Private / 1048576.0:F2} -> {fp1.Private / 1048576.0:F2} MB; handles {fp0.Handles} -> {fp1.Handles}; threads {fp0.Threads} -> {fp1.Threads}; hook installs {rig.WinEvents.Installs}, unhooks {rig.WinEvents.Unhooks}");
            stopMs.Max().ShouldBeLessThan(2000, "Stop returns well inside its bound when nothing is wedged");
            ((long)fp1.Handles - fp0.Handles).ShouldBeLessThanOrEqualTo(10);
            (fp1.Threads - fp0.Threads).ShouldBeLessThanOrEqualTo(2, "no thread survives Stop");
            rig.WinEvents.Installs.ShouldBe(rig.WinEvents.Unhooks, "every hook installed was removed");
        });

    [RealExplorerFact]
    public void NFR01_Start_stop_cycles_do_not_leak_handles_over_100_cycles() =>
        Scn.Run("handle-trend", rec =>
        {
            using var lab = new ExplorerLab("handles");
            using var rig = new TrackerRig(startNow: false, heartbeat: false);
            string a = lab.Mk("HT");
            nint w1 = lab.OpenWindow(a);
            lab.Foreground(w1).ShouldBeTrue();
            var samples = new List<(int Cycle, uint Handles, int Threads, long Private)>();
            for (int i = 0; i <= 100; i++)
            {
                rig.Tracker.Start().ShouldBe(StartResult.Started);
                rig.Observer.Await(s => s.FrameHwnd == w1, 0, 4000).Ms.ShouldNotBeNull();
                rig.Tracker.Stop();
                if (i % 20 == 0)
                {
                    var fp = Footprint();
                    samples.Add((i, fp.Handles, fp.Threads, fp.Private));
                }
            }

            foreach (var s in samples)
            {
                rec.Line($"after {s.Cycle} cycles: handles {s.Handles}, threads {s.Threads}, private {s.Private / 1048576.0:F2} MB");
            }

            double slope = (samples[^1].Handles - (double)samples[1].Handles) / (samples[^1].Cycle - samples[1].Cycle);
            rec.Line($"Handle growth from cycle 20 to 100: {samples[^1].Handles - samples[1].Handles} ({slope:F3} per cycle); hook installs {rig.WinEvents.Installs}, unhooks {rig.WinEvents.Unhooks}");
            (samples[^1].Handles - samples[1].Handles).ShouldBeLessThanOrEqualTo(10u);
        });

    /// <summary>
    /// O-25 / D4: what happens against a genuinely hung Explorer process. The process is a <b>separate</b> explorer.exe that the
    /// lab started with <c>/separate</c> for its own window; the shell process is never touched. Guards: the target must not be
    /// the shell process and must host no window that existed before the test; a detached helper resumes it after 45 s even if
    /// this test process dies; the window is closed and the process ended at the end.
    /// </summary>
    [RealExplorerFact]
    public void O25_A_hung_Explorer_process_is_cancelled_and_the_tracker_keeps_serving_other_windows() =>
        Scn.Run("hung-explorer", rec =>
        {
            using var lab = new ExplorerLab("hung");
            string p = lab.Mk("HungHost"), q = lab.Mk("HungOther");
            nint w3 = lab.OpenWindow(p, separateProcess: true);
            uint pid = LabNative.PidOf(w3);
            pid.ShouldNotBe(lab.ShellPid, "the window to hang must not be in the shell process");
            foreach (var pre in lab.Preexisting)
            {
                LabNative.PidOf(pre).ShouldNotBe(pid, "the process to hang must host no window that was open before the test");
            }

            using (var proc = Process.GetProcessById((int)pid))
            {
                proc.ProcessName.ShouldBe("explorer");
            }

            ExplorerLab.CommandLine((int)pid).ShouldContain("/factory", Case.Insensitive, "it must be the lab's own factory process");
            nint w1 = lab.OpenWindow(q);
            using var rig = new TrackerRig();
            lab.Foreground(w3).ShouldBeTrue();
            rig.Observer.Await(s => Scn.At(s, w3, p), 0, 5000).Ms.ShouldNotBeNull("a healthy start");
            rig.Ui.ResetResponsiveness();

            var guardian = StartGuardian(pid, rec, lab);
            nint hProc = LabNative.OpenProcess(LabNative.PROCESS_SUSPEND_RESUME, false, pid);
            hProc.ShouldNotBe(0);
            var samples = new List<string>();
            bool resumed = false;
            double followMs = double.NaN;
            double recoverMs = double.NaN;
            try
            {
                var s0 = rig.Tracker.GetStats();
                long suspendedAt = Stopwatch.GetTimestamp();
                LabNative.NtSuspendProcess(hProc).ShouldBe(0);
                for (int i = 0; i < 6; i++)
                {
                    Thread.Sleep(500);
                    var st = rig.Tracker.GetStats();
                    samples.Add($"t+{Stopwatch.GetElapsedTime(suspendedAt).TotalMilliseconds:F0} ms: cancels {st.CancelCalls}, max cancel {st.MaxCancelCallMs:F2} ms, replacements {st.Replacements}, abandoned {st.AbandonedWorkers}, state {st.State}, published {Scn.Describe(rig.Tracker.Current)}");
                }

                // While w3's process is frozen, move to a healthy window: the tracker must follow.
                var r = Scn.Step(rig, () => lab.Foreground(w1), s => Scn.At(s, w1, q), settleMs: 200, timeoutMs: 6000);
                followMs = r.Ms ?? double.NaN;
                samples.Add($"foreground to the healthy window while the other process is frozen: {(r.Ms is null ? "FAIL " + Scn.Describe(r.Seen) : r.Ms.Value.ToString("F0") + " ms")}");

                // Back to the frozen one: must not wedge the UI or the process.
                lab.Foreground(w3);
                Thread.Sleep(3000);
                var st2 = rig.Tracker.GetStats();
                samples.Add($"frozen window in the foreground for 3 s: cancels {st2.CancelCalls}, max cancel {st2.MaxCancelCallMs:F2} ms, replacements {st2.Replacements}, abandoned {st2.AbandonedWorkers}, state {st2.State}, published {Scn.Describe(rig.Tracker.Current)}");

                long resumeAt = Stopwatch.GetTimestamp();
                LabNative.NtResumeProcess(hProc);
                resumed = true;
                var rec2 = rig.Observer.Await(s => Scn.At(s, w3, p), resumeAt, 10000);
                recoverMs = rec2.Ms ?? double.NaN;
                samples.Add($"after resume: {(rec2.Ms is null ? "no recovery, saw " + Scn.Describe(rec2.Seen) : "back on the window's folder after " + rec2.Ms.Value.ToString("F0") + " ms")}");
            }
            finally
            {
                if (!resumed)
                {
                    LabNative.NtResumeProcess(hProc);
                }

                LabNative.CloseHandle(hProc);
                try
                {
                    guardian.Kill();
                }
                catch (InvalidOperationException)
                {
                    // already ended
                }

                guardian.Dispose();
            }

            foreach (var line in samples)
            {
                rec.Line(line);
            }

            var stats = rig.Tracker.GetStats();
            var ui = rig.Ui.Responsiveness();
            var sw = Stopwatch.StartNew();
            rig.Tracker.Stop();
            double stopMs = sw.Elapsed.TotalMilliseconds;
            rec.Line($"Totals: cancels {stats.CancelCalls}, MAX CancelCallMs {stats.MaxCancelCallMs:F2}, replacements {stats.Replacements}, abandoned workers {stats.AbandonedWorkers}, can retry {stats.CanRetry}, state {stats.State}/{stats.DisabledReason}; evaluation max {stats.EvaluationMaxMs:F0} ms; UI heartbeat p99 {ui.P99:F2} ms, max {ui.Max:F2} ms; Stop {stopMs:F0} ms; sequence regressions {rig.Observer.SequenceRegressions}; follow {followMs:F0} ms; recover {recoverMs:F0} ms; log: {rig.LogText()}");
            stats.MaxCancelCallMs.ShouldBeLessThan(50, "O-25: a cancel over 50 ms triggers the contingency");
            ui.Max.ShouldBeLessThan(250, "the UI thread is never blocked by the hung process");
            stopMs.ShouldBeLessThan(2500);
            rig.Observer.SequenceRegressions.ShouldBe(0);
            double.IsNaN(followMs).ShouldBeFalse("the tracker must follow to a healthy window while another process is frozen");
        });

    private static Process StartGuardian(uint pid, Rec rec, ExplorerLab lab)
    {
        // A detached helper: after 45 s, or when this test process is gone, resume the lab's own process. Harmless when already resumed.
        var script = Path.Combine(lab.Root, "guardian.ps1");
        File.WriteAllText(script, $$"""
            Add-Type -Namespace G -Name N -MemberDefinition '[DllImport("ntdll.dll")] public static extern int NtResumeProcess(System.IntPtr h);' -ErrorAction SilentlyContinue
            Add-Type -Namespace G -Name K -MemberDefinition '[DllImport("kernel32.dll")] public static extern System.IntPtr OpenProcess(uint a, bool i, uint p);' -ErrorAction SilentlyContinue
            $deadline = (Get-Date).AddSeconds(45)
            while ((Get-Date) -lt $deadline -and (Get-Process -Id {{Environment.ProcessId}} -ErrorAction SilentlyContinue)) { Start-Sleep -Milliseconds 500 }
            $h = [G.K]::OpenProcess(0x800, $false, {{pid}})
            if ($h -ne [IntPtr]::Zero) { [void][G.N]::NtResumeProcess($h) }
            """);
        var p = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"") { UseShellExecute = false, CreateNoWindow = true })!;
        rec.Line($"Guardian started (pid {p.Id}) to resume process {pid} after 45 s or when the test process ends.");
        return p;
    }
}
