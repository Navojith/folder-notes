using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Serilog;

namespace FolderNotes.App.Services;

/// <summary>
/// Gives memory back once the app is tray-only again (REQ-080, specs/02 § App shell). An idle tray
/// process allocates nothing, so the GC never runs on its own. Until it does, the managed wrappers
/// of a closed window, and the native XAML objects they hold, stay alive.
/// </summary>
internal sealed class IdleTrim(DispatcherQueue ui, ILogger log)
{
    public static readonly TimeSpan Delay = TimeSpan.FromSeconds(3);

    private readonly DispatcherQueue _ui = ui ?? throw new ArgumentNullException(nameof(ui));
    private readonly ILogger _log = (log ?? throw new ArgumentNullException(nameof(log))).ForContext<IdleTrim>();
    private DispatcherQueueTimer? _timer;

    /// <summary>Call when the last window closed. <paramref name="stillIdle"/> is checked again when the timer fires.</summary>
    public void Schedule(Func<bool> stillIdle)
    {
        _timer ??= _ui.CreateTimer();
        _timer.Stop();
        _timer.Interval = Delay;
        _timer.IsRepeating = false;
        _timer.Tick += OnTick;

        void OnTick(DispatcherQueueTimer sender, object args)
        {
            sender.Tick -= OnTick;
            if (stillIdle())
            {
                Trim();
            }
        }

        _timer.Start();
    }

    private void Trim()
    {
        var before = GC.GetTotalMemory(false);
        // Two passes: the first runs finalizers that release native XAML objects, the second frees what they held.
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        using var self = Process.GetCurrentProcess();
        _log.Information("Idle trim: managed heap {Before:N0} → {After:N0} KB, private bytes {Private:N0} KB",
            before / 1024, GC.GetTotalMemory(false) / 1024, self.PrivateMemorySize64 / 1024);
    }
}
