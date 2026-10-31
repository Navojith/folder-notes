using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FolderNotes.Core;
using Serilog;
using static FolderNotes.App.Shell.TrayNative;

namespace FolderNotes.App.Shell;

/// <summary>Tray menu entries (specs/05 § Tray).</summary>
internal enum TrayCommand
{
    Open = 1,
    Search = 2,
    TogglePanel = 3,
    Settings = 4,
    Quit = 5,
}

/// <summary>
/// The notification-area icon (REQ-080, ADR-012): a hidden top-level window on the UI thread that
/// owns the icon, shows the menu and re-adds the icon after an Explorer restart.
/// Must be created and disposed on the UI thread.
/// </summary>
internal sealed unsafe class TrayIcon : IDisposable
{
    private const uint CallbackMessage = WM_APP + 1;
    private const uint IconId = 1;
    private const string ClassName = "FolderNotes.TrayWindow";

    // One tray icon per process (single instance), so the window proc can find it statically.
    private static TrayIcon? s_current;

    private readonly ILogger _log;
    private readonly uint _taskbarCreated;
    private readonly IntPtr _instance;
    private IntPtr _hwnd;
    private IntPtr _icon;
    private bool _added;

    public TrayIcon(ILogger log)
    {
        _log = (log ?? throw new ArgumentNullException(nameof(log))).ForContext<TrayIcon>();
        if (s_current is not null)
        {
            throw new InvalidOperationException("Only one TrayIcon per process.");
        }

        _instance = GetModuleHandleW(null);
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");

        fixed (char* name = ClassName)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = &WndProc,
                hInstance = _instance,
                lpszClassName = name,
            };
            if (RegisterClassExW(wc) == 0)
            {
                throw new InvalidOperationException($"RegisterClassEx failed ({Marshal.GetLastPInvokeError()}).");
            }
        }

        s_current = this;

        // Top-level (not HWND_MESSAGE) so the TaskbarCreated broadcast reaches us. Never shown.
        _hwnd = CreateWindowExW(WS_EX_TOOLWINDOW, ClassName, AppInfo.ProductName, WS_POPUP, 0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, _instance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            s_current = null;
            throw new InvalidOperationException($"CreateWindowEx failed ({Marshal.GetLastPInvokeError()}).");
        }

        _icon = LoadTrayIcon();
        Add();
    }

    /// <summary>Raised on the UI thread for a click or a menu choice.</summary>
    public event EventHandler<TrayCommand>? CommandInvoked;

    /// <summary>Whether "Panel on/off" is enabled in the menu (Phase 3 turns it on).</summary>
    public bool PanelAvailable { get; set; }

    /// <summary>The hidden window; tests and diagnostics only.</summary>
    public IntPtr WindowHandle => _hwnd;

    public void Dispose()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        Remove();
        _ = DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
        if (_icon != IntPtr.Zero)
        {
            _ = DestroyIcon(_icon);
            _icon = IntPtr.Zero;
        }

        fixed (char* name = ClassName)
        {
            _ = UnregisterClassW(name, _instance);
        }

        s_current = null;
    }

    private void Add()
    {
        var data = NewData(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        _added = Shell_NotifyIconW(NIM_ADD, data);
        if (!_added)
        {
            // The taskbar may not exist yet at login; TaskbarCreated will retry.
            _log.Warning("Shell_NotifyIcon(NIM_ADD) failed; waiting for TaskbarCreated");
            return;
        }

        data.uVersion = NOTIFYICON_VERSION_4;
        _ = Shell_NotifyIconW(NIM_SETVERSION, data);
        _log.Debug("Tray icon added");
    }

    private void Remove()
    {
        if (_added)
        {
            _ = Shell_NotifyIconW(NIM_DELETE, NewData(0));
            _added = false;
        }
    }

    private NOTIFYICONDATAW NewData(uint flags)
    {
        var data = new NOTIFYICONDATAW
        {
            cbSize = (uint)sizeof(NOTIFYICONDATAW),
            hWnd = _hwnd,
            uID = IconId,
            uFlags = flags,
            uCallbackMessage = CallbackMessage,
            hIcon = _icon,
        };
        var tip = AppInfo.ProductName.AsSpan();
        tip[..Math.Min(tip.Length, 127)].CopyTo(new Span<char>(data.szTip, 128));
        return data;
    }

    private IntPtr LoadTrayIcon()
    {
        var dpi = GetDpiForSystem();
        var cx = GetSystemMetricsForDpi(SM_CXSMICON, dpi);
        var cy = GetSystemMetricsForDpi(SM_CYSMICON, dpi);
        var icon = LoadImageW(_instance, AppIconResourceId, IMAGE_ICON, cx, cy, LR_DEFAULTCOLOR);
        if (icon == IntPtr.Zero)
        {
            _log.Warning("App icon resource not found; using the stock application icon");
            icon = LoadImageW(IntPtr.Zero, IDI_APPLICATION, IMAGE_ICON, cx, cy, 0x8000 /* LR_SHARED */);
        }

        return icon;
    }

    private IntPtr OnMessage(uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == CallbackMessage)
        {
            // NOTIFYICON_VERSION_4: LOWORD(lParam) = event, wParam = anchor point in screen coordinates.
            var evt = (uint)(ushort)LowWord(lParam);
            if (evt is NIN_SELECT or NIN_KEYSELECT)
            {
                Raise(TrayCommand.Search); // 05 § Tray: left-click opens search.
            }
            else if (evt == WM_CONTEXTMENU)
            {
                ShowMenu(LowWord(wParam), HighWord(wParam));
            }

            return IntPtr.Zero;
        }

        if (msg == _taskbarCreated && _taskbarCreated != 0)
        {
            _log.Information("Explorer restarted (TaskbarCreated); re-adding the tray icon");
            _added = false;
            Add();
            return IntPtr.Zero;
        }

        return DefWindowProcW(_hwnd, msg, wParam, lParam);
    }

    private void ShowMenu(int x, int y)
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            _ = AppendMenuW(menu, MF_STRING, (nuint)TrayCommand.Open, "Open Folder Notes");
            _ = AppendMenuW(menu, MF_STRING, (nuint)TrayCommand.Search, "Search…");
            _ = AppendMenuW(menu, MF_STRING | (PanelAvailable ? 0 : MF_GRAYED), (nuint)TrayCommand.TogglePanel, "Panel on/off");
            _ = AppendMenuW(menu, MF_STRING, (nuint)TrayCommand.Settings, "Settings");
            _ = AppendMenuW(menu, MF_SEPARATOR, 0, null);
            _ = AppendMenuW(menu, MF_STRING, (nuint)TrayCommand.Quit, "Quit");
            _ = SetMenuDefaultItem(menu, (uint)TrayCommand.Open, 0);

            if (x == 0 && y == 0 && GetCursorPos(out var pt))
            {
                (x, y) = (pt.X, pt.Y);
            }

            // Documented tray-menu dance: foreground first so the menu closes on outside click,
            // then WM_NULL afterwards.
            _ = SetForegroundWindow(_hwnd);
            var chosen = TrackPopupMenuEx(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_NONOTIFY, x, y, _hwnd, IntPtr.Zero);
            _ = PostMessageW(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
            if (chosen > 0)
            {
                Raise((TrayCommand)chosen);
            }
        }
        finally
        {
            _ = DestroyMenu(menu);
        }
    }

    private void Raise(TrayCommand command)
    {
        _log.Debug("Tray command {Command}", command);
        CommandInvoked?.Invoke(this, command);
    }

    [UnmanagedCallersOnly]
    private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            var tray = s_current;
            if (tray is not null && tray._hwnd == hwnd)
            {
                return tray.OnMessage(msg, wParam, lParam);
            }
        }
#pragma warning disable CA1031 // Exceptions must never unwind into user32.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Log.Error(ex, "Tray window procedure failed");
        }

        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }
}
