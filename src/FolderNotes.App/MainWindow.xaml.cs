using FolderNotes.App.Library;
using FolderNotes.App.Themes;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Serilog;

namespace FolderNotes.App;

/// <summary>
/// The main window (specs/05 § Main window, 02 § App shell). Closing hides it so it can be shown
/// again without leaking a destroyed window's native memory (ADR-015); the detail editor is
/// flushed first.
/// </summary>
public sealed partial class MainWindow : Window, IThemeTarget
{
    private const int LogicalWidth = 1200;
    private const int LogicalHeight = 760;

    private readonly ThemeService _theme;
    private readonly ILogger _log;
    private bool _closingForGood;

    internal MainWindow(
        ThemeService theme,
        LibraryViewModel? library,
        Labels.LabelsViewModel? labels,
        Orphans.OrphansViewModel? orphans,
        Settings.SettingsViewModel? settings,
        ILogger log)
    {
        _theme = theme ?? throw new ArgumentNullException(nameof(theme));
        _log = (log ?? throw new ArgumentNullException(nameof(log))).ForContext<MainWindow>();
        LibraryViewModel = library;
        LabelsViewModel = labels;
        OrphansViewModel = orphans;
        SettingsViewModel = settings;
        InitializeComponent();
        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "FolderNotes.ico"));
        AppWindow.Resize(Scale(LogicalWidth, LogicalHeight));
        Library.ViewModel = library;
        Library.Visibility = library is null ? Visibility.Collapsed : Visibility.Visible;
        Nav.SelectedItem = AllNotesItem;
        AppWindow.Closing += OnClosing;
        if (orphans is not null)
        {
            orphans.Picker = new Services.WindowFolderPicker(() => AppWindow.Id);
            orphans.PropertyChanged += OnOrphansChanged;
        }

        OrphansItem.Visibility = orphans is null ? Visibility.Collapsed : Visibility.Visible;
        SettingsItem.Visibility = settings is null ? Visibility.Collapsed : Visibility.Visible;
        if (settings is not null)
        {
            settings.ManageLabelsRequested += (_, _) => Nav.SelectedItem = LabelsItem;
        }
    }

    /// <summary>The user closed the window; it's hidden now.</summary>
    internal event EventHandler? Hidden;

    internal LibraryViewModel? LibraryViewModel { get; }

    internal Labels.LabelsViewModel? LabelsViewModel { get; }

    internal Orphans.OrphansViewModel? OrphansViewModel { get; }

    internal Settings.SettingsViewModel? SettingsViewModel { get; }

    // The navigation badge (05, Orphaned page): the count, also in the item's UIA name.
    private void OnOrphansChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Orphans.OrphansViewModel.Count) && OrphansViewModel is { } orphans)
        {
            OrphansItem.InfoBadge = orphans.Count > 0 ? new InfoBadge { Value = orphans.Count } : null;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(OrphansItem, orphans.NavName);
        }
    }

    internal bool IsShown => AppWindow.IsVisible;

    void IThemeTarget.ApplyTheme(AppTheme theme)
    {
        Root.RequestedTheme = ThemeService.ToElementTheme(theme);
        AppWindow.TitleBar.PreferredTheme = theme switch
        {
            AppTheme.Light => TitleBarTheme.Light,
            AppTheme.Dark => TitleBarTheme.Dark,
            _ => TitleBarTheme.UseDefaultAppMode,
        };
    }

    // Created on first visit (EnsurePage).
    private Labels.LabelsPage? LabelsPage { get; set; }

    private Orphans.OrphansPage? OrphansPage { get; set; }

    private Settings.SettingsPage? SettingsPage { get; set; }

    /// <summary>
    /// Pages other than All notes are created on first visit, to keep memory down (REQ-080). They're
    /// built in code: x:Load can't realize elements declared directly in a Window (FindName returned
    /// null, which crashed the Labels and Settings pages since T2.6; found live in T2.7).
    /// </summary>
    private void EnsurePage(object page)
    {
        if (ReferenceEquals(page, LabelsItem) && LabelsPage is null && LabelsViewModel is not null)
        {
            LabelsPage = AddPage(new Labels.LabelsPage());
            LabelsPage.ViewModel = LabelsViewModel;
            LabelsPage.ShowNotesRequested += OnShowNotesRequested;
        }
        else if (ReferenceEquals(page, OrphansItem) && OrphansPage is null && OrphansViewModel is not null)
        {
            OrphansPage = AddPage(new Orphans.OrphansPage());
            OrphansPage.ViewModel = OrphansViewModel;
        }
        else if (ReferenceEquals(page, SettingsItem) && SettingsPage is null && SettingsViewModel is not null)
        {
            SettingsPage = AddPage(new Settings.SettingsPage());
            SettingsPage.ViewModel = SettingsViewModel;
        }
    }

    private T AddPage<T>(T page)
        where T : FrameworkElement
    {
        PageHost.Children.Add(page);
        return page;
    }

    /// <summary>Shows (or re-shows) the window and refreshes the library (05: relative times, first load).</summary>
    internal async Task ShowAsync()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore();
        }

        Activate();
        if (OrphansViewModel is { } orphans)
        {
            _ = RefreshOrphansAsync(orphans); // the badge; doesn't hold up the library
        }

        if (LibraryViewModel is { } library)
        {
            await library.ActivateAsync();
        }
    }

    private async Task RefreshOrphansAsync(Orphans.OrphansViewModel orphans)
    {
        try
        {
            await orphans.ReloadAsync();
        }
#pragma warning disable CA1031 // The badge is a hint; the page shows errors when opened.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log.Error(ex, "Counting the orphaned notes failed");
        }
    }

    /// <summary>The tray's Settings (05 § Settings page).</summary>
    internal void ShowSettings() => Nav.SelectedItem = SettingsItem;

    /// <summary>Switches to All notes (e.g. for <c>--open-note</c>).</summary>
    internal void ShowLibrary() => Nav.SelectedItem = AllNotesItem;

    internal void ShowRequest(string text)
    {
        RequestBar.Title = text;
        RequestBar.IsOpen = true;
    }

    internal void ShowError(string? message)
    {
        ErrorBar.Message = message ?? string.Empty;
        ErrorBar.IsOpen = message is not null;
    }

    /// <summary>Quit: flush the detail editor, then destroy the window.</summary>
    internal async Task CloseForGoodAsync()
    {
        await FlushAsync();
        _closingForGood = true;
        Close();
    }

    private async Task FlushAsync()
    {
        try
        {
            if (LibraryViewModel is { } library)
            {
                await library.FlushAsync();
            }
        }
#pragma warning disable CA1031 // Never trap the user in a window; the editor already logged and showed the error.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log.Error(ex, "Flushing the main window's editor failed");
        }
    }

    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closingForGood)
        {
            return;
        }

        args.Cancel = true;
        await FlushAsync();
        AppWindow.Hide();
        Hidden?.Invoke(this, EventArgs.Empty);
    }

    private async void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var page = args.SelectedItem;
        EnsurePage(page);
        if (ReferenceEquals(page, SettingsItem))
        {
            SettingsViewModel?.Refresh(); // the Run entry and the hotkey can change outside the page
        }

        if (SettingsPage is not null)
        {
            SettingsPage.Visibility = ReferenceEquals(page, SettingsItem) ? Visibility.Visible : Visibility.Collapsed;
        }

        Library.Visibility = ReferenceEquals(page, AllNotesItem) && LibraryViewModel is not null ? Visibility.Visible : Visibility.Collapsed;
        if (LabelsPage is not null)
        {
            LabelsPage.Visibility = ReferenceEquals(page, LabelsItem) && LabelsViewModel is not null ? Visibility.Visible : Visibility.Collapsed;
        }

        if (OrphansPage is not null)
        {
            OrphansPage.Visibility = ReferenceEquals(page, OrphansItem) && OrphansViewModel is not null ? Visibility.Visible : Visibility.Collapsed;
        }

        if (ReferenceEquals(page, OrphansItem) && OrphansViewModel is { } orphans)
        {
            try
            {
                await orphans.ActivateAsync(); // checks the listed notes again (05)
            }
#pragma warning disable CA1031 // Shown in the window rather than crashing the tray.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _log.Error(ex, "Loading the Orphaned page failed");
                ShowError($"Couldn't load the orphaned notes: {ex.Message}");
            }
        }

        if (ReferenceEquals(page, LabelsItem) && LabelsViewModel is { } labels)
        {
            try
            {
                await labels.ActivateAsync();
            }
#pragma warning disable CA1031 // Shown in the window rather than crashing the tray.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _log.Error(ex, "Loading the Labels page failed");
                ShowError($"Couldn't load the labels: {ex.Message}");
            }
        }
    }

    // "Show notes" on the Labels page: All notes, filtered to that label alone (05 § Labels page).
    private async void OnShowNotesRequested(object? sender, string labelName)
    {
        if (LibraryViewModel is not { } library)
        {
            return;
        }

        ShowLibrary();
        try
        {
            await library.ShowOnlyLabelAsync(labelName);
        }
#pragma warning disable CA1031 // See OnNavSelectionChanged.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log.Error(ex, "Filtering the library by {Label} failed", labelName);
            ShowError($"Couldn't show the notes: {ex.Message}");
        }
    }

    private Windows.Graphics.SizeInt32 Scale(int width, int height)
    {
        var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var scale = Microsoft.UI.Win32Interop.GetWindowFromWindowId(AppWindow.Id) is var hwnd && hwnd != 0
            ? NativeDpi.GetDpiForWindow(hwnd) / 96.0
            : 1.0;
        var work = display.WorkArea;
        return new Windows.Graphics.SizeInt32(
            Math.Min((int)(width * scale), work.Width),
            Math.Min((int)(height * scale), work.Height));
    }

    private static partial class NativeDpi
    {
        [System.Runtime.InteropServices.LibraryImport("user32.dll")]
        public static partial uint GetDpiForWindow(nint hwnd);
    }
}
