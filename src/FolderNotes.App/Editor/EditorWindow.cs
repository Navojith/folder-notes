using System.Runtime.InteropServices;
using FolderNotes.App.Themes;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Serilog;
using Windows.Graphics;
using Windows.System;

namespace FolderNotes.App.Editor;

/// <summary>
/// The compact editor window (specs/05 § Note editor, 02 § Editor): 560×640, Mica, centred on the
/// foreground Explorer window. Every close path flushes the view model first (REQ-005). Closing
/// hides the window so <see cref="AppShell"/> can reuse it: destroying WinUI windows doesn't give
/// their native memory back (REQ-080, found in T2.3).
/// </summary>
public sealed partial class EditorWindow : Window, IThemeTarget
{
    private const int LogicalWidth = 560;
    private const int LogicalHeight = 640;

    private readonly Grid _root = new() { Padding = new Thickness(24, 16, 24, 20) };
    private readonly ILogger _log;
    private bool _flushed;
    private bool _closingForGood;

    internal EditorWindow(NoteEditorViewModel viewModel, ILogger log)
    {
        _log = (log ?? throw new ArgumentNullException(nameof(log))).ForContext<EditorWindow>();
        SystemBackdrop = new MicaBackdrop();
        View = new NoteEditorView();
        View.CloseRequested += async (_, _) => await FlushAndCloseAsync(explicitSave: false);
        _root.Children.Add(View);
        Content = _root;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "FolderNotes.ico"));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMinimizable = false;
        }

        _root.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler(OnPreviewKeyDown), handledEventsToo: false);
        _root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnKeyDown), handledEventsToo: false);
        AppWindow.Closing += OnClosing;
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        Attach(viewModel);
    }

    /// <summary>The user closed the window; it's hidden now and can be reused or closed for good.</summary>
    internal event EventHandler? Hidden;

    internal NoteEditorViewModel ViewModel { get; private set; }

    internal NoteEditorView View { get; }

    void IThemeTarget.ApplyTheme(AppTheme theme)
    {
        _root.RequestedTheme = ThemeService.ToElementTheme(theme);
        AppWindow.TitleBar.PreferredTheme = theme switch
        {
            AppTheme.Light => TitleBarTheme.Light,
            AppTheme.Dark => TitleBarTheme.Dark,
            _ => TitleBarTheme.UseDefaultAppMode,
        };
    }

    /// <summary>Shows another session in this window (a fresh one, or a reused hidden window).</summary>
    internal void Attach(NoteEditorViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ViewModel.PropertyChanged -= OnViewModelChanged;
        ViewModel = viewModel;
        ViewModel.PropertyChanged += OnViewModelChanged;
        View.ViewModel = viewModel;
        View.ResetForNewSession();
        _flushed = false;
        UpdateTitle();
        PlaceNearExplorer();
    }

    /// <summary>Flushes and closes (hides). Used by Esc, Ctrl+Enter, the undo timeout and Quit.</summary>
    internal async Task FlushAndCloseAsync(bool explicitSave)
    {
        await FlushOnceAsync(explicitSave);
        HideForReuse();
    }

    /// <summary>Destroys the window. The session must already be flushed (Quit).</summary>
    internal void CloseForGood()
    {
        _closingForGood = true;
        Close();
    }

    internal void ShowAndFocus()
    {
        Activate();
        View.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, View.FocusTitle);
    }

    private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(NoteEditorViewModel.Title) or nameof(NoteEditorViewModel.State))
        {
            UpdateTitle();
        }
    }

    private void UpdateTitle()
    {
        var t = ViewModel.State == EditorState.Editing && ViewModel.Title.Trim() is { Length: > 0 } title ? title : "Folder note";
        Title = t + " · Folder Notes";
    }

    private async Task FlushOnceAsync(bool explicitSave)
    {
        if (_flushed)
        {
            return;
        }

        try
        {
            await (explicitSave ? ViewModel.SaveNowAsync() : ViewModel.FlushAsync());
        }
#pragma warning disable CA1031 // Never trap the user in a window; the view model already logged and showed the error.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log.Error(ex, "Flush on close failed");
        }

        _flushed = true;
    }

    private void HideForReuse()
    {
        if (!AppWindow.IsVisible)
        {
            return;
        }

        AppWindow.Hide();
        Hidden?.Invoke(this, EventArgs.Empty);
    }

    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closingForGood)
        {
            return;
        }

        args.Cancel = true;
        await FlushOnceAsync(explicitSave: false);
        HideForReuse();
    }

    private async void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Tunnelling, so the body TextBox doesn't turn Ctrl+Enter into a new line first.
        if (e.Key == VirtualKey.Enter && IsCtrlDown())
        {
            e.Handled = true;
            await FlushAndCloseAsync(explicitSave: true);
        }
    }

    private async void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Bubbling, so an open suggestion list, combo box or calendar handles Esc first.
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            await FlushAndCloseAsync(explicitSave: false);
        }
    }

    private static bool IsCtrlDown() =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    // ───────────── placement ─────────────

    private void PlaceNearExplorer()
    {
        var anchor = ExplorerRect();
        DisplayArea area = anchor is { } r
            ? DisplayArea.GetFromRect(r, DisplayAreaFallback.Nearest)
            : DisplayArea.GetFromPoint(CursorPosition(), DisplayAreaFallback.Primary);
        var scale = MonitorScale(area);
        var size = new SizeInt32((int)(LogicalWidth * scale), (int)(LogicalHeight * scale));
        var work = area.WorkArea;
        size.Width = Math.Min(size.Width, work.Width);
        size.Height = Math.Min(size.Height, work.Height);

        var center = anchor is { } a
            ? new PointInt32(a.X + (a.Width / 2), a.Y + (a.Height / 2))
            : new PointInt32(work.X + (work.Width / 2), work.Y + (work.Height / 2));
        var x = Math.Clamp(center.X - (size.Width / 2), work.X, work.X + work.Width - size.Width);
        var y = Math.Clamp(center.Y - (size.Height / 2), work.Y, work.Y + work.Height - size.Height);
        AppWindow.MoveAndResize(new RectInt32(x, y, size.Width, size.Height));
    }

    /// <summary>The foreground window's rectangle if it's a File Explorer window.</summary>
    private static RectInt32? ExplorerRect()
    {
        var hwnd = Native.GetForegroundWindow();
        if (hwnd == 0)
        {
            return null;
        }

        int length;
        Span<char> buffer = stackalloc char[64];
        unsafe
        {
            fixed (char* p = buffer)
            {
                length = Native.GetClassName(hwnd, p, buffer.Length);
            }
        }

        if (length <= 0 || !buffer[..length].SequenceEqual("CabinetWClass") || !Native.GetWindowRect(hwnd, out var rect))
        {
            return null;
        }

        return new RectInt32(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    private static PointInt32 CursorPosition() =>
        Native.GetCursorPos(out var p) ? new PointInt32(p.X, p.Y) : new PointInt32(0, 0);

    private static double MonitorScale(DisplayArea area)
    {
        var monitor = Win32Interop.GetMonitorFromDisplayId(area.DisplayId);
        return Native.GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0 && dpiX > 0 ? dpiX / 96.0 : 1.0;
    }

    private static partial class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Point
        {
            public int X;
            public int Y;
        }

        [LibraryImport("user32.dll")]
        public static partial nint GetForegroundWindow();

        [LibraryImport("user32.dll", EntryPoint = "GetClassNameW")]
        public static unsafe partial int GetClassName(nint hwnd, char* className, int maxCount);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool GetWindowRect(nint hwnd, out Rect rect);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool GetCursorPos(out Point point);

        [LibraryImport("shcore.dll")]
        public static partial int GetDpiForMonitor(nint monitor, int dpiType, out uint dpiX, out uint dpiY);
    }
}
