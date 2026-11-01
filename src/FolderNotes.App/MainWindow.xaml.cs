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
    private bool _syncingChoice;
    private AppTheme _appTheme;
    private bool _closingForGood;

    internal MainWindow(ThemeService theme, LibraryViewModel? library, Labels.LabelsViewModel? labels, ILogger log)
    {
        _theme = theme ?? throw new ArgumentNullException(nameof(theme));
        _log = (log ?? throw new ArgumentNullException(nameof(log))).ForContext<MainWindow>();
        LibraryViewModel = library;
        LabelsViewModel = labels;
        InitializeComponent();
        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "FolderNotes.ico"));
        AppWindow.Resize(Scale(LogicalWidth, LogicalHeight));
        Library.ViewModel = library;
        Library.Visibility = library is null ? Visibility.Collapsed : Visibility.Visible;
        Nav.SelectedItem = AllNotesItem;
        AppWindow.Closing += OnClosing;
    }

    /// <summary>The user closed the window; it's hidden now.</summary>
    internal event EventHandler? Hidden;

    internal LibraryViewModel? LibraryViewModel { get; }

    internal Labels.LabelsViewModel? LabelsViewModel { get; }

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
        _appTheme = theme;
        SyncThemeChoice();
    }

    private void SyncThemeChoice()
    {
        if (ThemeChoice is null)
        {
            return; // the Settings page isn't created until it's first opened (x:Load)
        }

        _syncingChoice = true;
        ThemeChoice.SelectedIndex = (int)_appTheme;
        _syncingChoice = false;
    }

    /// <summary>Pages other than All notes are created on first visit (x:Load), to keep memory down (REQ-080).</summary>
    private void EnsurePage(object page)
    {
        if (ReferenceEquals(page, LabelsItem) && LabelsPage is null && LabelsViewModel is not null)
        {
            _ = Root.FindName(nameof(LabelsPage));
            LabelsPage!.ViewModel = LabelsViewModel;
            LabelsPage.ShowNotesRequested += OnShowNotesRequested;
        }
        else if (ReferenceEquals(page, SettingsItem) && SettingsPage is null)
        {
            _ = Root.FindName(nameof(SettingsPage));
            SyncThemeChoice();
        }
    }

    /// <summary>Shows (or re-shows) the window and refreshes the library (05: relative times, first load).</summary>
    internal async Task ShowAsync()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore();
        }

        Activate();
        if (LibraryViewModel is { } library)
        {
            await library.ActivateAsync();
        }
    }

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
        if (SettingsPage is not null)
        {
            SettingsPage.Visibility = ReferenceEquals(page, SettingsItem) ? Visibility.Visible : Visibility.Collapsed;
        }

        Library.Visibility = ReferenceEquals(page, AllNotesItem) && LibraryViewModel is not null ? Visibility.Visible : Visibility.Collapsed;
        if (LabelsPage is not null)
        {
            LabelsPage.Visibility = ReferenceEquals(page, LabelsItem) && LabelsViewModel is not null ? Visibility.Visible : Visibility.Collapsed;
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

    private void OnThemeChoiceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncingChoice && ThemeChoice.SelectedIndex >= 0)
        {
            _theme.Theme = (AppTheme)ThemeChoice.SelectedIndex;
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
