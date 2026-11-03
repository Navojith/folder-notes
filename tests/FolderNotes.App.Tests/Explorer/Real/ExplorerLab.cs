using System.Diagnostics;
using System.Reflection;
using System.Text;
using FolderNotes.App.Explorer;
using FolderNotes.App.Explorer.Native;

namespace FolderNotes.App.Tests.Explorer.Real;

/// <summary>
/// Gate for the real-Explorer verification (06 § Real-Explorer verification protocol). Off unless asked for, and since T3.2.5's
/// first sweep the value must be <c>disposable</c>: during that sweep the shell (explorer.exe) of the dev desktop crashed with an
/// access violation in SHELL32.dll while these tests were running (09 O-26). Until that is understood the suite belongs on a
/// disposable session or VM, not on a desktop someone is working on.
/// </summary>
public static class RealExplorerGate
{
    public static bool Enabled =>
        Environment.GetEnvironmentVariable("FOLDERNOTES_REAL_EXPLORER") == "disposable" && Environment.UserInteractive;
}

/// <summary>A fact that only runs with <c>FOLDERNOTES_REAL_EXPLORER=disposable</c> on an interactive desktop.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RealExplorerFactAttribute : FactAttribute
{
    public RealExplorerFactAttribute([System.Runtime.CompilerServices.CallerFilePath] string? sourceFilePath = null, [System.Runtime.CompilerServices.CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = "Real-Explorer verification: set FOLDERNOTES_REAL_EXPLORER=disposable on an interactive DISPOSABLE session (it crashed the shell once, 09 O-26).";
        Timeout = 900_000;
        SkipUnless = nameof(RealExplorerGate.Enabled);
        SkipType = typeof(RealExplorerGate);
    }
}

/// <summary>Measurement helpers.</summary>
internal static class Stat
{
    public static double Percentile(IReadOnlyList<double> xs, double p)
    {
        if (xs.Count == 0)
        {
            return double.NaN;
        }

        var s = xs.OrderBy(x => x).ToList();
        return s[Math.Clamp((int)Math.Ceiling(p / 100.0 * s.Count) - 1, 0, s.Count - 1)];
    }

    public static string Summary(IReadOnlyList<double> xs) =>
        xs.Count == 0 ? "n=0" : $"n={xs.Count}, median {Percentile(xs, 50):F1} ms, p95 {Percentile(xs, 95):F1} ms, max {xs.Max():F1} ms";
}

/// <summary>Results go to <c>artifacts/t325/</c> (git-ignored); the report in specs is written from them.</summary>
internal sealed class Rec(string name)
{
    private readonly StringBuilder _text = new();

    public void Line(string text)
    {
        lock (_text)
        {
            _text.AppendLine(text);
        }

        Console.WriteLine($"[{name}] {text}");
    }

    public void Flush()
    {
        var dir = Path.Combine(RepoRoot(), "artifacts", "t325");
        Directory.CreateDirectory(dir);
        lock (_text)
        {
            File.WriteAllText(Path.Combine(dir, name + ".md"), _text.ToString());
        }
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "FolderNotes.slnx")))
        {
            d = d.Parent;
        }

        return d?.FullName ?? Path.GetTempPath();
    }
}

internal sealed record Obs(long Ts, ActiveFolderState State);

/// <summary>Records every <c>ActiveFolderChanged</c> as the UI thread sees it, with a timestamp taken in the handler.</summary>
internal sealed class Observer
{
    private readonly object _gate = new();
    private readonly List<Obs> _events = [];
    private long _lastSequence = long.MinValue;

    public Observer(IExplorerTracker tracker)
    {
        Tracker = tracker;
        tracker.ActiveFolderChanged += (_, e) =>
        {
            long ts = Stopwatch.GetTimestamp();
            lock (_gate)
            {
                if (e.State.Sequence < _lastSequence)
                {
                    SequenceRegressions++;
                }

                _lastSequence = Math.Max(_lastSequence, e.State.Sequence);
                _events.Add(new Obs(ts, e.State));
                Monitor.PulseAll(_gate);
            }
        };
    }

    public IExplorerTracker Tracker { get; }

    public int SequenceRegressions { get; private set; }

    public int Count
    {
        get { lock (_gate) { return _events.Count; } }
    }

    public List<Obs> Snapshot()
    {
        lock (_gate)
        {
            return [.. _events];
        }
    }

    public ActiveFolderState Last
    {
        get { lock (_gate) { return _events.Count == 0 ? ActiveFolderState.Inactive() : _events[^1].State; } }
    }

    /// <summary>
    /// Waits for an event, delivered at or after <paramref name="startTs"/>, that satisfies the predicate. Returns the
    /// latency from <paramref name="startTs"/> to its delivery on the UI thread, or null on timeout.
    /// </summary>
    public (double? Ms, ActiveFolderState Seen) Await(Func<ActiveFolderState, bool> predicate, long startTs, int timeoutMs = 4000)
    {
        long deadline = Stopwatch.GetTimestamp() + (Stopwatch.Frequency * timeoutMs / 1000);
        int next = 0;
        lock (_gate)
        {
            while (true)
            {
                for (; next < _events.Count; next++)
                {
                    var e = _events[next];
                    if (e.Ts >= startTs && predicate(e.State))
                    {
                        return (Stopwatch.GetElapsedTime(startTs, e.Ts).TotalMilliseconds, e.State);
                    }
                }

                long left = deadline - Stopwatch.GetTimestamp();
                if (left <= 0)
                {
                    return (null, _events.Count == 0 ? ActiveFolderState.Inactive() : _events[^1].State);
                }

                Monitor.Wait(_gate, TimeSpan.FromMilliseconds(Math.Max(1, left * 1000.0 / Stopwatch.Frequency)));
            }
        }
    }
}

/// <summary>A tracker plus everything around it that the tests need to measure.</summary>
internal sealed class TrackerRig : IDisposable
{
    public TrackerRig(Func<uint, bool>? dropEvents = null, bool installNothing = false, ExplorerTrackerOptions? options = null, bool startNow = true, bool heartbeat = true, bool skipOwnProcess = true, Func<IWin32Shell, IWin32Shell>? wrapShell = null)
    {
        Ui = new LabUi(heartbeat);
        WinEvents = new LabWinEvents(new Win32WinEventApi(skipOwnProcess), dropEvents, installNothing);
        Guard = new TrackerProcessGuard();
        Tracker = new ExplorerTracker(Ui, win: wrapShell?.Invoke(new Win32Shell()), winEvents: WinEvents, guard: Guard, options: options, log: m => { lock (Log) { Log.Add(m); } });
        Observer = new Observer(Tracker);
        if (startNow)
        {
            StartResult = Tracker.Start();
        }
    }

    public LabUi Ui { get; }

    public LabWinEvents WinEvents { get; }

    public TrackerProcessGuard Guard { get; }

    public ExplorerTracker Tracker { get; }

    public Observer Observer { get; }

    public List<string> Log { get; } = [];

    public StartResult StartResult { get; }

    public string LogText()
    {
        lock (Log)
        {
            return string.Join(" | ", Log);
        }
    }

    public void Dispose()
    {
        Tracker.Dispose();
        Ui.Dispose();
    }
}

/// <summary>
/// Opens, drives and closes Explorer windows <b>of its own</b> and reads Explorer's own idea of where each tab is (through
/// the late-bound <c>Shell.Application</c> object, an oracle independent of the tracker's typed interop).
/// It never closes a window it did not open and never touches the shell process (09 D5, T3.2.5 safety rules).
/// </summary>
internal sealed class ExplorerLab : IDisposable
{
    private readonly object _shell;
    private readonly HashSet<nint> _preexisting;
    private readonly HashSet<nint> _owned = [];
    private readonly HashSet<int> _explorerPidsBefore;
    private readonly List<string> _substDrives = [];
    private readonly List<string> _netDrives = [];

    public ExplorerLab(string name)
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
        {
            throw new InvalidOperationException("ExplorerLab needs an STA thread (use Sta.Run).");
        }

        Root = Path.Combine("C:\\Temp", "FolderNotesE2E", "T325", name);
        Directory.CreateDirectory(Root);
        _shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
        _preexisting = Frames();
        _explorerPidsBefore = [.. Process.GetProcessesByName("explorer").Select(p => p.Id)];
        ShellPid = LabNative.PidOf(LabNative.GetShellWindow());
    }

    public string Root { get; }

    public uint ShellPid { get; }

    public IReadOnlyCollection<nint> Owned => _owned;

    public IReadOnlyCollection<nint> Preexisting => _preexisting;

    public string Mk(string relative) => Directory.CreateDirectory(Path.Combine(Root, relative)).FullName;

    // ---- oracle -------------------------------------------------------------------------------------------

    private static object? Get(object o, string name) => o.GetType().InvokeMember(name, BindingFlags.GetProperty, null, o, null);

    private static object? Call(object o, string name, params object?[] args) => o.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, o, args);

    public List<object> Browsers()
    {
        var list = new List<object>();
        var ws = Call(_shell, "Windows")!;
        int n = (int)Get(ws, "Count")!;
        for (int i = 0; i < n; i++)
        {
            try
            {
                if (Call(ws, "Item", i) is { } w)
                {
                    list.Add(w);
                }
            }
            catch (Exception)
            {
                // a window closed while enumerating
            }
        }

        return list;
    }

    public static nint Hwnd(object browser) => (nint)Convert.ToInt64(Get(browser, "HWND"));

    public HashSet<nint> Frames()
    {
        var set = new HashSet<nint>();
        foreach (var b in Browsers())
        {
            try
            {
                set.Add(Hwnd(b));
            }
            catch (Exception)
            {
                // gone
            }
        }

        return set;
    }

    public List<object> TabsOf(nint frame) => [.. Browsers().Where(b => { try { return Hwnd(b) == frame; } catch (Exception) { return false; } })];

    /// <summary>Explorer's own path for a tab (<c>Document.Folder.Self.Path</c>); virtual folders give a <c>::{guid}</c> string.</summary>
    public static string? OraclePath(object tab)
    {
        try
        {
            var doc = Get(tab, "Document");
            var folder = doc is null ? null : Get(doc, "Folder");
            var self = folder is null ? null : Get(folder, "Self");
            return self is null ? null : (string?)Get(self, "Path");
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Navigate2 on a tab; Explorer answers 0x800700AA (busy) when it is mid-navigation, so retry briefly.</summary>
    public static void Navigate(object tab, string target)
    {
        for (int i = 0; ; i++)
        {
            try
            {
                Call(tab, "Navigate2", target);
                return;
            }
            catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is System.Runtime.InteropServices.COMException && i < 50)
            {
                Thread.Sleep(20);
            }
        }
    }

    // ---- windows ------------------------------------------------------------------------------------------

    /// <summary>Opens a new window on <paramref name="path"/> (in the shell's process, or in its own with <paramref name="separateProcess"/>).</summary>
    public nint OpenWindow(string path, bool separateProcess = false, int timeoutMs = 8000)
    {
        var before = Frames();
        var arg = separateProcess ? $"/separate,\"{path}\"" : $"\"{path}\"";
        Process.Start(new ProcessStartInfo("explorer.exe", arg) { UseShellExecute = false })?.Dispose();
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            Thread.Sleep(80);
            var fresh = Frames().Except(before).FirstOrDefault();
            if (fresh != 0)
            {
                _owned.Add(fresh);
                Thread.Sleep(350);
                return fresh;
            }
        }

        throw new TimeoutException($"No Explorer window appeared for {path}.");
    }

    /// <summary>Ctrl+N in a frame the lab owns opens another window in the <b>same process</b>.</summary>
    public nint NewWindowFromFrame(nint frame, int timeoutMs = 8000)
    {
        Require(frame);
        var before = Frames();
        if (!LabNative.ForceForeground(frame))
        {
            throw new InvalidOperationException("Could not bring the lab window to the foreground; not sending keys.");
        }

        Thread.Sleep(120);
        LabNative.Chord(LabNative.VK_CONTROL, LabNative.VK_N);
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            Thread.Sleep(80);
            var fresh = Frames().Except(before).FirstOrDefault();
            if (fresh != 0)
            {
                _owned.Add(fresh);
                Thread.Sleep(350);
                return fresh;
            }
        }

        throw new TimeoutException("Ctrl+N opened no window.");
    }

    public bool Foreground(nint frame)
    {
        Require(frame);
        return LabNative.ForceForeground(frame);
    }

    /// <summary>Opens a tab with Ctrl+T on a frame the lab owns; returns the new tab object.</summary>
    public object NewTab(nint frame)
    {
        Require(frame);
        var before = TabsOf(frame);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (!LabNative.ForceForeground(frame))
            {
                throw new InvalidOperationException("Could not bring the lab window to the foreground; not sending keys.");
            }

            Thread.Sleep(120);
            LabNative.Chord(LabNative.VK_CONTROL, LabNative.VK_T);
            for (int i = 0; i < 80; i++)
            {
                Thread.Sleep(50);
                var now = TabsOf(frame);
                if (now.Count > before.Count)
                {
                    Thread.Sleep(200);
                    return TabsOf(frame).First(t => !before.Any(b => ReferenceEquals(b, t)));
                }
            }
        }

        throw new TimeoutException($"The new tab did not register (had {before.Count} tabs).");
    }

    /// <summary>Sends keys to a frame the lab owns, after checking that it really is the foreground window.</summary>
    public void Keys(nint frame, params byte[] vks)
    {
        Require(frame);
        if (LabNative.GetForegroundWindow() != frame)
        {
            throw new InvalidOperationException($"Refusing to send keys: the foreground is {LabNative.Describe(LabNative.GetForegroundWindow())}, not the lab window.");
        }

        LabNative.Chord(vks);
    }

    public void Close(nint frame)
    {
        Require(frame);
        LabNative.PostMessage(frame, LabNative.WM_CLOSE, 0, 0);
        for (int i = 0; i < 60 && LabNative.IsWindow(frame); i++)
        {
            Thread.Sleep(50);
        }

        _owned.Remove(frame);
    }

    private void Require(nint frame)
    {
        if (!_owned.Contains(frame))
        {
            throw new InvalidOperationException($"Window {LabNative.Describe(frame)} was not opened by the lab; refusing to touch it.");
        }
    }

    // ---- drives -------------------------------------------------------------------------------------------

    private static string FreeDriveLetter()
    {
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        for (char c = 'Z'; c >= 'E'; c--)
        {
            if (!used.Contains(c))
            {
                return c + ":";
            }
        }

        throw new InvalidOperationException("No free drive letter.");
    }

    /// <summary>A <c>subst</c> drive for <paramref name="folder"/> (a mapped-drive stand-in with no network).</summary>
    public string? Subst(string folder)
    {
        var letter = FreeDriveLetter();
        return RunTool("subst.exe", $"{letter} \"{folder}\"") == 0 ? Track(_substDrives, letter) : null;
    }

    /// <summary>A real network-redirector drive to the loopback admin share, if the machine allows it.</summary>
    public string? NetUse(string share)
    {
        var letter = FreeDriveLetter();
        return RunTool("net.exe", $"use {letter} {share} /persistent:no") == 0 ? Track(_netDrives, letter) : null;
    }

    private static string Track(List<string> list, string letter)
    {
        list.Add(letter);
        return letter;
    }

    private static int RunTool(string exe, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
        if (p is null)
        {
            return -1;
        }

        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit(15000);
        return p.HasExited ? p.ExitCode : -1;
    }

    // ---- cleanup ------------------------------------------------------------------------------------------

    /// <summary>Explorer processes that appeared since the lab started and are not the shell (separate-process windows).</summary>
    public List<int> NewExplorerProcesses() =>
        [.. Process.GetProcessesByName("explorer").Select(p => p.Id).Where(id => !_explorerPidsBefore.Contains(id) && id != ShellPid)];

    public void Dispose()
    {
        foreach (var f in _owned.ToArray())
        {
            try
            {
                Close(f);
            }
            catch (Exception)
            {
                // best effort
            }
        }

        foreach (var d in _substDrives)
        {
            RunTool("subst.exe", $"{d} /d");
        }

        foreach (var d in _netDrives)
        {
            RunTool("net.exe", $"use {d} /delete /y");
        }

        // A separate-process window leaves its "-Embedding /factory" host behind for a while. It is the lab's own
        // process: only a process that did not exist at the start, is not the shell, and has that exact command line.
        Thread.Sleep(500);
        foreach (var pid in NewExplorerProcesses())
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (CommandLine(pid).Contains("/factory", StringComparison.OrdinalIgnoreCase) && p.Id != ShellPid && !_explorerPidsBefore.Contains(p.Id))
                {
                    p.Kill();
                }
            }
            catch (Exception)
            {
                // already gone
            }
        }
    }

    public static string CommandLine(int pid)
    {
        var info = new ProcessStartInfo("powershell.exe", $"-NoProfile -Command \"(Get-CimInstance Win32_Process -Filter 'ProcessId={pid}').CommandLine\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };
        using var p = Process.Start(info);
        if (p is null)
        {
            return string.Empty;
        }

        var text = p.StandardOutput.ReadToEnd();
        p.WaitForExit(15000);
        return text.Trim();
    }
}

internal static class Sta
{
    public static void Run(Action body)
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        }
    }
}
