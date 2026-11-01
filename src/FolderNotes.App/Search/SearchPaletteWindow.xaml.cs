using System.ComponentModel;
using FolderNotes.App.Themes;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Serilog;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.System;

namespace FolderNotes.App.Search;

/// <summary>
/// The search palette window (specs/05 § Search palette behaviour, ADR-019): no title bar, not in
/// the taskbar or Alt+Tab, hidden (not destroyed) when it closes.
/// </summary>
public sealed partial class SearchPaletteWindow : Window, IThemeTarget
{
    private const int LogicalWidth = 640;

    private readonly ILogger _log;
    private bool _syncing;
    private bool _closingForGood;
    private bool _focusBoxOnActivate; // focus set before the window is really active gets lost (found live)

    internal SearchPaletteWindow(SearchPaletteViewModel viewModel, ILogger log)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _log = (log ?? throw new ArgumentNullException(nameof(log))).ForContext<SearchPaletteWindow>();
        InitializeComponent();
        AppWindow.IsShownInSwitchers = false;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
            presenter.IsResizable = false;
            presenter.IsMinimizable = false;
            presenter.IsMaximizable = false;
            presenter.IsAlwaysOnTop = true;
        }

        Root.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler(OnPreviewKeyDown), handledEventsToo: true);
        Activated += OnActivated;
        AppWindow.Closing += OnClosing;
        ViewModel.PropertyChanged += OnVmChanged;
        Sync();
    }

    internal SearchPaletteViewModel ViewModel { get; }

    /// <summary>The palette was hidden (Esc, lost focus, an open action).</summary>
    internal event EventHandler? Hidden;

    internal bool IsShown => AppWindow.IsVisible;

    void IThemeTarget.ApplyTheme(AppTheme theme) => Root.RequestedTheme = ThemeService.ToElementTheme(theme);

    /// <summary>Shows the palette on the active monitor (05) and runs <paramref name="query"/>.</summary>
    internal async Task ShowAsync(string query)
    {
        Place();
        _syncing = true;
        QueryBox.Text = query;
        _syncing = false;
        _focusBoxOnActivate = true;
        Activate();
        BringToForeground();
        FocusBox();
        await ViewModel.OpenAsync(query);
        FitHeight();
    }

    /// <summary>Quit.</summary>
    internal void CloseForGood()
    {
        _closingForGood = true;
        Close();
    }

    internal void HidePalette()
    {
        if (AppWindow.IsVisible)
        {
            AppWindow.Hide();
            Hidden?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Activate() can lose to the foreground lock when another app is in front (found live in T2.6),
    /// so join that window's input queue for a moment and take the foreground (ADR-019).
    /// </summary>
    private void BringToForeground()
    {
        var hwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == hwnd)
        {
            return;
        }

        var foregroundThread = NativeMethods.GetWindowThreadProcessId(foreground, 0);
        var ourThread = NativeMethods.GetCurrentThreadId();
        var attached = foregroundThread != 0 && foregroundThread != ourThread && NativeMethods.AttachThreadInput(ourThread, foregroundThread, true);
        try
        {
            _ = NativeMethods.BringWindowToTop(hwnd);
            var ok = NativeMethods.SetForegroundWindow(hwnd);
            _log.Debug("Palette foreground: {Ok} (attached to the foreground thread: {Attached})", ok, attached);
        }
        finally
        {
            if (attached)
            {
                _ = NativeMethods.AttachThreadInput(ourThread, foregroundThread, false);
            }
        }
    }

    // ───────────── placement ─────────────

    private void Place()
    {
        DisplayArea display;
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground != 0 && foreground != Microsoft.UI.Win32Interop.GetWindowFromWindowId(AppWindow.Id))
        {
            display = DisplayArea.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(foreground), DisplayAreaFallback.Nearest);
        }
        else
        {
            NativeMethods.GetCursorPos(out var pt);
            display = DisplayArea.GetFromPoint(new PointInt32(pt.X, pt.Y), DisplayAreaFallback.Nearest);
        }

        var work = display.WorkArea;
        var scale = Scale();
        var width = (int)(LogicalWidth * scale);
        var height = AppWindow.Size.Height > 0 ? AppWindow.Size.Height : (int)(120 * scale);
        AppWindow.MoveAndResize(new RectInt32(work.X + ((work.Width - width) / 2), work.Y + (int)(work.Height * 0.2), width, height));
    }

    /// <summary>Height fits the content (05): box, up to 8 rows, footer.</summary>
    private void FitHeight()
    {
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            Root.Measure(new Windows.Foundation.Size(LogicalWidth, double.PositiveInfinity));
            var scale = Scale();
            var height = (int)Math.Ceiling((Root.DesiredSize.Height + 2) * scale);
            if (Math.Abs(AppWindow.Size.Height - height) > 1)
            {
                AppWindow.Resize(new SizeInt32(AppWindow.Size.Width, height));
            }
        });
    }

    private double Scale()
    {
        var hwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        return hwnd != 0 ? NativeMethods.GetDpiForWindow(hwnd) / 96.0 : 1.0;
    }

    // ───────────── view model → controls ─────────────

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        Sync();
        if (e.PropertyName is nameof(SearchPaletteViewModel.Results) or nameof(SearchPaletteViewModel.Suggestions)
            or nameof(SearchPaletteViewModel.ErrorText) or nameof(SearchPaletteViewModel.EmptyText))
        {
            FitHeight();
        }
    }

    private void Sync()
    {
        var vm = ViewModel;
        if (!ReferenceEquals(ResultList.ItemsSource, vm.Results))
        {
            ResultList.ItemsSource = vm.Results;
        }

        if (ResultList.SelectedIndex != vm.SelectedIndex)
        {
            ResultList.SelectedIndex = vm.SelectedIndex;
        }

        if (vm.Selected is { } selected)
        {
            ResultList.ScrollIntoView(selected);
        }

        if (!ReferenceEquals(SuggestionList.ItemsSource, vm.Suggestions))
        {
            SuggestionList.ItemsSource = vm.Suggestions;
        }

        SuggestionList.SelectedIndex = vm.SuggestionIndex;
        SuggestionList.Visibility = vm.ShowSuggestions ? Visibility.Visible : Visibility.Collapsed;
        ResultList.Visibility = vm.ShowSuggestions || vm.Results.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        EmptyText.Text = vm.EmptyText ?? "";
        EmptyText.Visibility = !vm.ShowSuggestions && vm.EmptyText is not null ? Visibility.Visible : Visibility.Collapsed;
        ErrorText.Text = vm.ErrorText ?? "";
        ErrorText.Visibility = vm.ErrorText is null ? Visibility.Collapsed : Visibility.Visible;
        FooterText.Text = vm.FooterText;
        CountText.Text = vm.ShowSuggestions ? "" : vm.CountText;
    }

    // ───────────── input ─────────────

    private void OnQueryChanged(object sender, TextChangedEventArgs e)
    {
        if (!_syncing)
        {
            ViewModel.SetQuery(QueryBox.Text, QueryBox.SelectionStart);
        }
    }

    private void OnQuerySelectionChanged(object sender, RoutedEventArgs e)
    {
        if (!_syncing && QueryBox.Text == ViewModel.Query)
        {
            ViewModel.SetCaret(QueryBox.SelectionStart);
        }
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        switch (e.Key)
        {
            case VirtualKey.Down:
            case VirtualKey.Up:
                ViewModel.MoveSelection(e.Key == VirtualKey.Down ? 1 : -1);
                e.Handled = true;
                break;
            case VirtualKey.Tab when ViewModel.ShowSuggestions:
                Accept(null);
                e.Handled = true;
                break;
            case VirtualKey.Enter:
                e.Handled = true;
                if (ViewModel.ShowSuggestions)
                {
                    Accept(null);
                }
                else if (ctrl)
                {
                    if (ViewModel.OpenNote())
                    {
                        HidePalette();
                    }
                }
                else if (ViewModel.OpenFolder() == OpenFolderResult.Opened)
                {
                    HidePalette();
                }

                break;
            case VirtualKey.Escape:
                e.Handled = true;
                if (!ViewModel.DismissSuggestions())
                {
                    HidePalette();
                }

                break;
            case VirtualKey.C when ctrl && QueryBox.SelectionLength == 0:
                if (ViewModel.CopyPath() is { } path)
                {
                    var package = new DataPackage();
                    package.SetText(path);
                    Clipboard.SetContent(package);
                    e.Handled = true;
                }

                break;
        }
    }

    private void Accept(int? index)
    {
        if (ViewModel.AcceptSuggestion(index) is { } chosen)
        {
            _syncing = true;
            QueryBox.Text = chosen.NewText;
            QueryBox.SelectionStart = chosen.NewCaret;
            _syncing = false;
            QueryBox.Focus(FocusState.Programmatic);
        }
    }

    private void OnResultClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SearchResultRow row)
        {
            ViewModel.Select(ViewModel.Results.ToList().IndexOf(row));
            if (ViewModel.OpenFolder() == OpenFolderResult.Opened)
            {
                HidePalette();
            }
        }
    }

    private void OnSuggestionClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SearchSuggestion suggestion)
        {
            Accept(ViewModel.Suggestions.ToList().IndexOf(suggestion));
        }
    }

    // ───────────── lifetime ─────────────

    private void FocusBox()
    {
        QueryBox.Focus(FocusState.Programmatic);
        QueryBox.SelectAll();
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated && _focusBoxOnActivate)
        {
            _focusBoxOnActivate = false;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, FocusBox);
        }

        // 05: it closes when it loses focus.
        if (args.WindowActivationState == WindowActivationState.Deactivated && AppWindow.IsVisible)
        {
            _log.Debug("Palette lost focus; hiding");
            HidePalette();
        }
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!_closingForGood)
        {
            args.Cancel = true;
            HidePalette();
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct CursorPoint
    {
        public int X;
        public int Y;
    }

    private static partial class NativeMethods
    {
        [System.Runtime.InteropServices.LibraryImport("user32.dll")]
        public static partial nint GetForegroundWindow();

        [System.Runtime.InteropServices.LibraryImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public static partial bool GetCursorPos(out CursorPoint point);

        [System.Runtime.InteropServices.LibraryImport("user32.dll")]
        public static partial uint GetDpiForWindow(nint hwnd);

        [System.Runtime.InteropServices.LibraryImport("user32.dll")]
        public static partial uint GetWindowThreadProcessId(nint hwnd, nint processId);

        [System.Runtime.InteropServices.LibraryImport("kernel32.dll")]
        public static partial uint GetCurrentThreadId();

        [System.Runtime.InteropServices.LibraryImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public static partial bool AttachThreadInput(uint attach, uint attachTo, [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] bool doAttach);

        [System.Runtime.InteropServices.LibraryImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public static partial bool BringWindowToTop(nint hwnd);

        [System.Runtime.InteropServices.LibraryImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public static partial bool SetForegroundWindow(nint hwnd);
    }
}
