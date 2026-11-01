using FolderNotes.App.Services;
using FolderNotes.App.Shell;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;

namespace FolderNotes.App;

/// <summary>
/// Custom entry point (specs/02 § App shell): the single-instance check runs before any XAML is
/// created, so a redirected second launch stays cheap.
/// </summary>
internal static class Program
{
    public const string InstanceKey = "FolderNotes";

    private static readonly Lock s_gate = new();
    private static readonly List<string> s_pendingRedirects = [];
    private static Action<string>? s_redirectHandler;

    [STAThread]
    private static int Main(string[] args)
    {
        // Installer switches (ADR-024): explicit, headless, before anything else starts.
        if (ShellVerbCommand.TryHandle(args, new RegistryShellVerbRegistrar(Environment.ProcessPath ?? "FolderNotes.exe"), out var shellExit))
        {
            return shellExit;
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();

        var main = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (!main.IsCurrent)
        {
            Redirect(main);
            return 0;
        }

        main.Activated += OnRedirectedActivation;

        Application.Start(_p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App(args);
        });
        return 0;
    }

    /// <summary>
    /// Called by <see cref="App"/> once it can route commands (on the UI thread). Redirects that
    /// arrived earlier are replayed in order.
    /// </summary>
    public static void SetRedirectHandler(Action<string> handler)
    {
        string[] pending;
        lock (s_gate)
        {
            s_redirectHandler = handler;
            pending = [.. s_pendingRedirects];
            s_pendingRedirects.Clear();
        }

        foreach (var commandLine in pending)
        {
            handler(commandLine);
        }
    }

    private static void Redirect(AppInstance main)
    {
        // Let the main instance take the foreground; this process has the right, it doesn't.
        _ = TrayNative.AllowSetForegroundWindow(main.ProcessId);
        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();

        // Off the STA thread; the CLR's STA-aware wait keeps pumping COM while it completes.
        Task.Run(() => main.RedirectActivationToAsync(activation).AsTask()).Wait();
    }

    /// <summary>Runs on a thread-pool thread in the main instance.</summary>
    private static void OnRedirectedActivation(object? sender, AppActivationArguments e)
    {
        var commandLine = e.Kind == ExtendedActivationKind.Launch && e.Data is ILaunchActivatedEventArgs launch
            ? launch.Arguments ?? string.Empty
            : string.Empty;

        Action<string>? handler;
        lock (s_gate)
        {
            handler = s_redirectHandler;
            if (handler is null)
            {
                s_pendingRedirects.Add(commandLine);
                return;
            }
        }

        handler(commandLine);
    }
}
