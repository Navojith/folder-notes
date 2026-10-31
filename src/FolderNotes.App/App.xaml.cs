using FolderNotes.App.Activation;
using FolderNotes.App.Services;
using FolderNotes.App.Shell;
using FolderNotes.Core;
using FolderNotes.Core.Identity;
using FolderNotes.Core.Search;
using FolderNotes.Core.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Serilog;
using Serilog.Events;

namespace FolderNotes.App;

/// <summary>
/// The tray process (REQ-080, specs/02 § App shell): logging, composition, tray icon and command routing.
/// </summary>
public partial class App : Application
{
    private readonly string[] _args;
    private readonly ILogger _log;
    private ServiceProvider? _services;
    private readonly FolderIdentityService _identity = new();
    private SqliteNoteStore? _store;
    private AppShell? _shell;
    private CommandRouter? _router;
    private bool _quitting;

    internal App(string[] args)
    {
        _args = args;
        _log = Log.Logger = CreateLogger();
        _log.Information("Folder Notes {Version} starting (pid {Pid}, args: {Args})",
            typeof(App).Assembly.GetName().Version, Environment.ProcessId, args);

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Fatal(e.ExceptionObject as Exception, "Unhandled exception (AppDomain)");
            Log.CloseAndFlush();
        };
        TaskScheduler.UnobservedTaskException += (_, e) => Log.Error(e.Exception, "Unobserved task exception");
        UnhandledException += (_, e) => Log.Fatal(e.Exception, "Unhandled exception (XAML): {Message}", e.Message);

        InitializeComponent();

        // Closing the last window must not end the tray process; only Quit does.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            await StartAsync();
        }
#pragma warning disable CA1031 // async void entry point: log and keep the process explicable.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log.Fatal(ex, "Startup failed");
            Quit();
        }
    }

    private async Task StartAsync()
    {
        var ui = DispatcherQueue.GetForCurrentThread();
        _shell = new AppShell(_log);

        try
        {
            _store = await SqliteNoteStore.OpenAsync(new SqliteNoteStoreOptions
            {
                DatabasePath = Path.Combine(AppInfo.DataDirectory, AppInfo.DatabaseFileName),
                Identity = _identity,
            });
        }
#pragma warning disable CA1031 // Any failure here is shown to the user rather than crashing the tray.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log.Fatal(ex, "Could not open the notes database");
            _shell.StartupError = $"Couldn't open the notes database: {ex.Message}";
        }

        _services = ConfigureServices(ui);
        _router = _services.GetRequiredService<CommandRouter>();

        var tray = _services.GetRequiredService<TrayIcon>();
        tray.CommandInvoked += (_, command) => OnTrayCommand(command);

        // Our own args first, then anything a second launch forwarded meanwhile.
        _router.Route(_store is null ? new ActivationCommand.ShowMain() : ActivationParser.Parse(_args), ActivationSource.Startup);
        var exePath = Environment.ProcessPath ?? "FolderNotes.exe";
        Program.SetRedirectHandler(commandLine => ui.TryEnqueue(() =>
            _router.Route(ActivationParser.ParseCommandLine(commandLine, exePath), ActivationSource.Redirected)));

        _log.Information("Started in {Ms} ms", (DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalMilliseconds);
    }

    private ServiceProvider ConfigureServices(DispatcherQueue ui)
    {
        var services = new ServiceCollection();
        services.AddSingleton(_log);
        services.AddSingleton<IUiDispatcher>(new DispatcherQueueUiDispatcher(ui));
        services.AddSingleton(_shell!);
        services.AddSingleton<IAppShell>(_shell!);
        services.AddSingleton<CommandRouter>();
        services.AddSingleton<TrayIcon>();
        if (_store is not null)
        {
            // Registered as an instance so the container doesn't dispose it; Quit does, after draining.
            services.AddSingleton(_store);
            services.AddSingleton<INoteRepository>(_store);
            services.AddSingleton<ILabelRepository>(_store);
            services.AddSingleton<IFolderIdentityService>(_identity);
            services.AddSingleton<SearchService>();
            services.AddSingleton<ISearchService>(sp => sp.GetRequiredService<SearchService>());
            services.AddSingleton<UiNoteEvents>();
        }

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private void OnTrayCommand(TrayCommand command)
    {
        switch (command)
        {
            case TrayCommand.Open:
                _router?.Route(new ActivationCommand.ShowMain(), ActivationSource.Tray);
                break;
            case TrayCommand.Search:
                _router?.Route(new ActivationCommand.Search(string.Empty), ActivationSource.Tray);
                break;
            case TrayCommand.Settings:
                // T2.8 adds the Settings page.
                _router?.Route(new ActivationCommand.ShowMain(), ActivationSource.Tray);
                break;
            case TrayCommand.TogglePanel:
                break; // Phase 3.
            case TrayCommand.Quit:
                Quit();
                break;
        }
    }

    private async void Quit()
    {
        if (_quitting)
        {
            return;
        }

        _quitting = true;
        _log.Information("Quitting");
        try
        {
            _shell?.CloseMainWindow();
            if (_services is not null)
            {
                await _services.DisposeAsync(); // tray icon, search service, UI relay
            }

            if (_store is not null)
            {
                await _store.DisposeAsync(); // drains the writer queue
            }
        }
#pragma warning disable CA1031 // Shutting down regardless.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log.Error(ex, "Error during shutdown");
        }
        finally
        {
            await Log.CloseAndFlushAsync();
            Exit();
        }
    }

    private static Serilog.Core.Logger CreateLogger()
    {
        var config = new LoggerConfiguration()
#if DEBUG
            .MinimumLevel.Debug()
#else
            .MinimumLevel.Information()
#endif
            .Enrich.WithProperty("Pid", Environment.ProcessId)
            .WriteTo.File(
                Path.Combine(AppInfo.DataDirectory, "logs", "app-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                restrictedToMinimumLevel: LogEventLevel.Verbose,
                shared: true,
                formatProvider: System.Globalization.CultureInfo.InvariantCulture,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}");
        return config.CreateLogger();
    }
}
