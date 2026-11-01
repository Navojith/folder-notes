using CommunityToolkit.Mvvm.ComponentModel;
using FolderNotes.App.Services;
using FolderNotes.App.Themes;
using Serilog;

namespace FolderNotes.App.Settings;

/// <summary>
/// The Settings page (specs/05 § Settings page, 02 § Settings, REQ-080/081, ADR-022). Every change is
/// saved at once; a failed save puts the control back. UI-thread only; no XAML types.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    public static readonly TimeSpan SavedMessageTime = TimeSpan.FromSeconds(2);
    private const uint VkEscape = 0x1B;

    private readonly AppSettings _settings;
    private readonly Action<AppTheme> _applyTheme;
    private readonly IHotkeyHost _hotkeys;
    private readonly IStartupRegistration _startup;
    private readonly TimeProvider _clock;
    private readonly IUiDispatcher _ui;
    private readonly ILogger _log;
    private readonly Func<string, bool> _openFolder;
    private ITimer? _messageTimer;
    private bool _disposed;

    internal SettingsViewModel(
        AppSettings settings,
        Action<AppTheme> applyTheme,
        IHotkeyHost hotkeys,
        IStartupRegistration startup,
        TimeProvider clock,
        IUiDispatcher ui,
        ILogger log,
        string version,
        string dataFolder,
        Func<string, bool>? openFolder = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _applyTheme = applyTheme ?? throw new ArgumentNullException(nameof(applyTheme));
        _hotkeys = hotkeys ?? throw new ArgumentNullException(nameof(hotkeys));
        _startup = startup ?? throw new ArgumentNullException(nameof(startup));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _log = (log ?? throw new ArgumentNullException(nameof(log))).ForContext<SettingsViewModel>();
        _openFolder = openFolder ?? FolderLauncher.Open;
        Version = version;
        DataFolder = dataFolder;
        Theme = settings.Theme;
        Refresh();
    }

    /// <summary>Manage labels → the Labels page.</summary>
    public event EventHandler? ManageLabelsRequested;

    // ───────────── appearance ─────────────

    [ObservableProperty]
    public partial AppTheme Theme { get; private set; }

    [ObservableProperty]
    public partial string? ThemeError { get; private set; }

    public async Task SetThemeAsync(AppTheme theme)
    {
        if (theme == Theme && theme == _settings.Theme)
        {
            return;
        }

        var previous = _settings.Theme;
        Theme = theme;
        _applyTheme(theme);
        try
        {
            await _settings.SetThemeAsync(theme);
            ThemeError = null;
            _log.Information("Theme set to {Theme}", theme);
        }
#pragma warning disable CA1031 // Shown under the control, which goes back (05).
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log.Error(ex, "Saving the theme failed");
            Theme = previous;
            _applyTheme(previous);
            ThemeError = "Couldn't save the theme: " + ex.Message;
        }
    }

    // ───────────── search shortcut ─────────────

    /// <summary>The saved shortcut, "Ctrl + Alt + N".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HotkeyAccessibleName))]
    public partial string HotkeyText { get; private set; } = "";

    [ObservableProperty]
    public partial bool IsCapturing { get; private set; }

    /// <summary>"Saved", a refusal, or a capture hint; null when there's nothing to say.</summary>
    [ObservableProperty]
    public partial string? HotkeyMessage { get; private set; }

    [ObservableProperty]
    public partial bool HotkeyMessageIsError { get; private set; }

    /// <summary>The saved shortcut couldn't be registered (at startup, or later); null when it works.</summary>
    [ObservableProperty]
    public partial string? HotkeyWarning { get; private set; }

    public string HotkeyAccessibleName => "Search shortcut, " + HotkeyText;

    public static string DefaultHotkeyText => HotkeyGesture.Default.DisplayText;

    public void BeginCapture()
    {
        IsCapturing = true;
        ShowHotkeyMessage("Press the new shortcut… Esc to cancel.", isError: false, transient: false);
    }

    public void CancelCapture()
    {
        IsCapturing = false;
        HotkeyMessage = null;
    }

    /// <summary>A key press while capturing (05). True when it ended the capture.</summary>
    public async Task<bool> OnKeyPressAsync(uint modifiers, uint virtualKey)
    {
        if (!IsCapturing)
        {
            return false;
        }

        if (virtualKey == VkEscape && modifiers == 0)
        {
            CancelCapture();
            return true;
        }

        if (HotkeyGesture.FromKeyPress(modifiers, virtualKey) is not { } gesture)
        {
            if (!IsModifierKey(virtualKey))
            {
                ShowHotkeyMessage("Use Ctrl, Alt or Win with a letter, digit, F1–F24 or Space.", isError: true, transient: false);
            }

            return false;
        }

        IsCapturing = false;
        await ApplyHotkeyAsync(gesture);
        return true;
    }

    public Task ResetHotkeyAsync()
    {
        IsCapturing = false;
        return ApplyHotkeyAsync(HotkeyGesture.Default);
    }

    private async Task ApplyHotkeyAsync(HotkeyGesture gesture)
    {
        if (gesture == _settings.SearchHotkey && gesture == _hotkeys.Registered)
        {
            HotkeyMessage = null;
            return;
        }

        var previous = _hotkeys.Registered;
        if (!_hotkeys.TryChange(gesture))
        {
            _log.Information("Search shortcut {Shortcut} refused: another app owns it", gesture);
            ShowHotkeyMessage($"{gesture.DisplayText} is used by another app. Choose another.", isError: true, transient: false);
            Refresh();
            return;
        }

        try
        {
            await _settings.SetSearchHotkeyAsync(gesture);
        }
#pragma warning disable CA1031 // Shown; the previous shortcut is put back.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log.Error(ex, "Saving the search shortcut failed");
            if (previous is not null)
            {
                _hotkeys.TryChange(previous);
            }

            ShowHotkeyMessage("Couldn't save the shortcut: " + ex.Message, isError: true, transient: false);
            Refresh();
            return;
        }

        _log.Information("Search shortcut set to {Shortcut}", gesture);
        Refresh();
        ShowHotkeyMessage("Saved", isError: false, transient: true);
    }

    private void ShowHotkeyMessage(string text, bool isError, bool transient)
    {
        _messageTimer?.Dispose();
        _messageTimer = null;
        HotkeyMessage = text;
        HotkeyMessageIsError = isError;
        if (transient)
        {
            _messageTimer = _clock.CreateTimer(_ => _ui.Post(() =>
            {
                if (!_disposed && HotkeyMessage == text)
                {
                    HotkeyMessage = null;
                }
            }), null, SavedMessageTime, Timeout.InfiniteTimeSpan);
        }
    }

    private static bool IsModifierKey(uint vk) => vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or (>= 0xA0 and <= 0xA5);

    // ───────────── startup ─────────────

    [ObservableProperty]
    public partial bool StartWithWindows { get; private set; }

    [ObservableProperty]
    public partial string? StartupError { get; private set; }

    public void SetStartWithWindows(bool on)
    {
        try
        {
            if (on)
            {
                _startup.Enable();
            }
            else
            {
                _startup.Disable();
            }

            StartupError = null;
            _log.Information("Start with Windows turned {State}", on ? "on" : "off");
        }
#pragma warning disable CA1031 // Shown under the toggle, which goes back (05).
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log.Error(ex, "Changing start with Windows failed");
            StartupError = "Couldn't change this: " + ex.Message;
        }

        StartWithWindows = SafeIsEnabled();
    }

    // ───────────── labels, about ─────────────

    public string Version { get; }

    public string DataFolder { get; }

    public void ManageLabels() => ManageLabelsRequested?.Invoke(this, EventArgs.Empty);

    public bool OpenDataFolder() => _openFolder(DataFolder);

    // ───────────── state ─────────────

    /// <summary>Re-reads what can change outside the page (the hotkey's registration, the Run entry).</summary>
    public void Refresh()
    {
        var saved = _settings.SearchHotkey;
        HotkeyText = saved.DisplayText;
        HotkeyWarning = _hotkeys.Registered == saved
            ? null
            : $"{saved.DisplayText} couldn't be registered: another app uses it. Choose another, or close that app and restart Folder Notes.";
        StartWithWindows = SafeIsEnabled();
    }

    private bool SafeIsEnabled()
    {
        try
        {
            return _startup.IsEnabled;
        }
#pragma warning disable CA1031 // A registry read failure shows as "off".
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log.Error(ex, "Reading the startup entry failed");
            return false;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _messageTimer?.Dispose();
    }
}
