using FolderNotes.App.Activation;
using FolderNotes.App.Editor;
using FolderNotes.App.Services;
using FolderNotes.App.Shell;
using FolderNotes.App.Themes;
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
        var uiDispatcher = new DispatcherQueueUiDispatcher(ui);
        var theme = new ThemeService(new SystemHighContrastSignal(_log), uiDispatcher, _log);
        ThemeTokens.Service = theme; // T2.8 loads the saved theme setting before any window opens.
        _shell = new AppShell(theme, _log);
        _shell.IdleTrim = new IdleTrim(ui, _log);

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

        // Settings first, so the theme applies before any window opens (05 § Settings page, ADR-022).
        AppSettings? settings = null;
        if (_store is not null)
        {
            settings = new AppSettings(_store, _log);
            try
            {
                await settings.LoadAsync();
            }
#pragma warning disable CA1031 // Defaults are fine; the store logged the problem.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _log.Error(ex, "Loading the settings failed; using defaults");
            }

            theme.Theme = settings.Theme;
        }

        _services = ConfigureServices(uiDispatcher, theme);
        if (_store is not null)
        {
            var store = _store;
            _shell.EditorFactory = () => new NoteEditorViewModel(store, store, _identity, TimeProvider.System, uiDispatcher, _log);
        }

        if (_store is not null)
        {
            var store = _store;
            var events = _services.GetRequiredService<UiNoteEvents>();
            var editors = _shell.EditorFactory!;
            var appSettings = settings!;
            _shell.LibraryFactory = () =>
            {
                var library = new Library.LibraryViewModel(store, store, events, editors, TimeProvider.System, _log);
                library.Restore(appSettings.Library);
                library.StateChanged = state => _ = SaveLibraryStateAsync(appSettings, state);
                return library;
            };
            _shell.LabelsFactory = () => new Labels.LabelsViewModel(store, events, _log);

            // Orphan detection (03, Orphan detection): scheduled scans plus the Orphaned page.
            var scanner = _services.GetRequiredService<OrphanScanner>();
            var scans = _services.GetRequiredService<OrphanScanService>();
            scans.Start();
            _shell.OrphansFactory = () => new Orphans.OrphansViewModel(
                store, scanner, scans.ScanNowAsync, _identity, events, uiDispatcher, TimeProvider.System, _log);
            _shell.NoteEvents = events;
            var search = _services.GetRequiredService<ISearchService>();
            _shell.SearchFactory = () => new Search.SearchPaletteViewModel(search, store, uiDispatcher, TimeProvider.System, _log);
        }

        _router = _services.GetRequiredService<CommandRouter>();

        var tray = _services.GetRequiredService<TrayIcon>();
        tray.CommandInvoked += (_, command) => OnTrayCommand(command);
        tray.HotkeyPressed += (_, _) => _router?.Route(new ActivationCommand.Search(string.Empty), ActivationSource.Tray);
        // REQ-050: the saved shortcut (Ctrl+Alt+N by default). A failure shows in Settings (05).
        tray.TryChange(settings?.SearchHotkey ?? HotkeyGesture.Default);

        var startup = new RunKeyStartupRegistration(Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "FolderNotes.exe"));
        try
        {
            if (startup.RepairPath())
            {
                _log.Information("Start with Windows pointed at another copy; updated it to this exe (ADR-022)");
            }
        }
#pragma warning disable CA1031 // Not worth failing startup over.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log.Warning(ex, "Checking the start-with-Windows entry failed");
        }

        if (settings is not null)
        {
            var appSettings2 = settings;
            _shell.SettingsFactory = () => new Settings.SettingsViewModel(
                appSettings2, t => theme.Theme = t, tray, startup, TimeProvider.System, uiDispatcher, _log,
                typeof(App).Assembly.GetName().Version?.ToString(3) ?? "?", AppInfo.DataDirectory,
                shellVerbs: new RegistryShellVerbRegistrar(Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "FolderNotes.exe")));
        }

        // Our own args first, then anything a second launch forwarded meanwhile.
        _router.Route(_store is null ? new ActivationCommand.ShowMain() : ActivationParser.Parse(_args), ActivationSource.Startup);
        var exePath = Environment.ProcessPath ?? "FolderNotes.exe";
        Program.SetRedirectHandler(commandLine => ui.TryEnqueue(() =>
            _router.Route(ActivationParser.ParseCommandLine(commandLine, exePath), ActivationSource.Redirected)));

        _log.Information("Started in {Ms} ms", (DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalMilliseconds);
    }

    private ServiceProvider ConfigureServices(IUiDispatcher ui, ThemeService theme)
    {
        var services = new ServiceCollection();
        services.AddSingleton(_log);
        services.AddSingleton(ui);
        services.AddSingleton(theme); // lives as long as the process
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
            services.AddSingleton(sp => new OrphanScanner(sp.GetRequiredService<INoteRepository>(), _identity));
            services.AddSingleton(sp => new OrphanScanService(sp.GetRequiredService<OrphanScanner>().ScanAsync, TimeProvider.System, _log));
        }

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private async Task SaveLibraryStateAsync(AppSettings settings, Library.LibraryState state)
    {
        try
        {
            await settings.SetLibraryAsync(state);
        }
#pragma warning disable CA1031 // Losing a sort choice isn't worth an error dialog; logged.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log.Error(ex, "Saving the library view state failed");
        }
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
                _shell?.ShowSettings();
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
            if (_shell is not null)
            {
                await _shell.CloseEditorsAsync(); // saves pending edits (REQ-005)
                await _shell.CloseMainWindowAsync();
            }
            if (_services is not null)
            {
                await _services.DisposeAsync(); // tray icon, search service, UI relay, orphan scans
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
