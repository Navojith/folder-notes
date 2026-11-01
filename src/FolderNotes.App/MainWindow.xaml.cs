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
    private bool _closingForGood;

    internal MainWindow(ThemeService theme, LibraryViewModel? library, ILogger log)
    {
        _theme = theme ?? throw new ArgumentNullException(nameof(theme));
        _log = (log ?? throw new ArgumentNullException(nameof(log))).ForContext<MainWindow>();
        LibraryViewModel = library;
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
        _syncingChoice = true;
        ThemeChoice.SelectedIndex = (int)theme;
        _syncingChoice = false;
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

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var settings = ReferenceEquals(args.SelectedItem, SettingsItem);
        SettingsPage.Visibility = settings ? Visibility.Visible : Visibility.Collapsed;
        Library.Visibility = !settings && LibraryViewModel is not null ? Visibility.Visible : Visibility.Collapsed;
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
