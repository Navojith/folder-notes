using System.Diagnostics;
using FolderNotes.App.Explorer;

namespace FolderNotes.App.Tests.Explorer.Real;

/// <summary>Shared steps for the real-Explorer tests (06 § Real-Explorer verification protocol).</summary>
internal static class Scn
{
    public static bool At(ActiveFolderState s, nint frame, string? path) =>
        s.FrameHwnd == frame && string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase);

    public static bool Inactive(ActiveFolderState s) => s.IsInactive;

    /// <summary>
    /// Runs <paramref name="action"/>, then waits for the first event delivered on the UI thread afterwards that satisfies
    /// <paramref name="expect"/>. The latency is measured from just before the action to the handler on the UI thread, so
    /// it includes Explorer's own work, the WinEvent, the evaluation and the hand-off.
    /// </summary>
    public static (double? Ms, ActiveFolderState Seen) Step(TrackerRig rig, Action action, Func<ActiveFolderState, bool> expect, int settleMs = 150, int timeoutMs = 4000)
    {
        long ts = Stopwatch.GetTimestamp();
        action();
        var result = rig.Observer.Await(expect, ts, timeoutMs);
        Thread.Sleep(settleMs);
        return result;
    }

    public static string Describe(ActiveFolderState s) =>
        s.IsInactive ? "Inactive" : $"frame 0x{s.FrameHwnd:X}, path {(s.Path is null ? "(null)" : s.Path)}";

    /// <summary>Explorer's frame title starts with the active tab's name ("B and 2 more tabs - File Explorer").</summary>
    public static bool TitleShows(nint frame, string path)
    {
        var title = LabNative.Title(frame);
        return title.StartsWith(Leaf(path), StringComparison.OrdinalIgnoreCase) && (title.Length == Leaf(path).Length || !char.IsLetterOrDigit(title[Leaf(path).Length]));
    }

    /// <summary>
    /// Explorer works through queued keys after the sender is done, so its title keeps changing for a while. Waits until the
    /// title has not changed for <paramref name="quietMs"/> (at most 30 s) and returns how long that took.
    /// </summary>
    public static double SettleTitle(nint frame, int quietMs = 2000)
    {
        var sw = Stopwatch.StartNew();
        var last = LabNative.Title(frame);
        var lastChange = sw.ElapsedMilliseconds;
        while (sw.ElapsedMilliseconds < 30_000 && sw.ElapsedMilliseconds - lastChange < quietMs)
        {
            Thread.Sleep(50);
            var t = LabNative.Title(frame);
            if (t != last)
            {
                last = t;
                lastChange = sw.ElapsedMilliseconds;
            }
        }

        return lastChange;
    }

    public static string Leaf(string path)
    {
        var trimmed = path.TrimEnd('\\');
        var i = trimmed.LastIndexOf('\\');
        return i < 0 ? trimmed : trimmed[(i + 1)..];
    }

    [ThreadStatic]
    private static List<TrackerRig>? _rigs;

    internal static void Register(TrackerRig rig) => (_rigs ??= []).Add(rig);

    /// <summary>
    /// Runs a test body on an STA thread and always writes the recorded lines. This desktop is in use by a person while the
    /// tests run, and Windows lets them (or any app) take the foreground at any moment. If an attempt fails <b>and</b> a
    /// foreground change to a foreign window was seen during it, the attempt is discarded as contaminated and repeated
    /// (at most 3 attempts); the report names the attempt that produced the numbers.
    /// </summary>
    public static void Run(string name, Action<Rec> body)
    {
        var rec = new Rec(name);
        try
        {
            for (int attempt = 1; ; attempt++)
            {
                int foreign = 0;
                Exception? failure = null;
                try
                {
                    _rigs = [];
                    Sta.Run(() =>
                    {
                        _rigs = [];
                        try
                        {
                            body(rec);
                        }
                        finally
                        {
                            foreign = _rigs.Sum(r => r.WinEvents.ForeignForegrounds);
                        }
                    });
                }
                catch (Exception ex) when (ex is not Xunit.Sdk.XunitException || ex.GetType().Name != "DynamicSkipException")
                {
                    failure = ex;
                }

                rec.Line($"Attempt {attempt}: {(failure is null ? "passed" : "FAILED")}; foreign foreground changes seen during the attempt: {foreign}");
                if (failure is null)
                {
                    return;
                }

                if (foreign == 0 || attempt >= 3)
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
                }

                rec.Line("  -> contaminated by another app taking the foreground; repeating.");
            }
        }
        finally
        {
            rec.Flush();
        }
    }
}

/// <summary>Everything in this collection runs one at a time: there is one desktop and one tracker per process.</summary>
[CollectionDefinition("RealExplorer", DisableParallelization = true)]
public sealed class RealExplorerCollection;
