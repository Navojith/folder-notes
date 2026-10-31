using Microsoft.UI.Xaml;
using Serilog;

namespace FolderNotes.App.Themes;

/// <summary>The app theme setting (REQ-081). <see cref="System"/> follows the Windows app mode live.</summary>
public enum AppTheme
{
    System,
    Light,
    Dark,
}

/// <summary>Something that shows themed UI: a window, in practice (specs/05 § Design tokens, Theme).</summary>
internal interface IThemeTarget
{
    void ApplyTheme(AppTheme theme);
}

/// <summary>Whether a Windows high-contrast theme is on. Abstracted so the service can be tested without XAML.</summary>
internal interface IHighContrastSignal
{
    bool IsOn { get; }

    /// <summary>Raised on any thread when <see cref="IsOn"/> may have changed.</summary>
    event EventHandler? Changed;
}

/// <summary>
/// Holds the app theme and applies it to every registered window (specs/05 § Design tokens).
/// Also tracks high contrast so controls that look tokens up in code can refresh. UI thread only,
/// except the high-contrast signal, which is marshalled through <see cref="Services.IUiDispatcher"/>.
/// </summary>
internal sealed class ThemeService : IDisposable
{
    private readonly List<IThemeTarget> _targets = [];
    private readonly IHighContrastSignal _highContrast;
    private readonly Services.IUiDispatcher _ui;
    private readonly ILogger _log;
    private AppTheme _theme;

    public ThemeService(IHighContrastSignal highContrast, Services.IUiDispatcher ui, ILogger log)
    {
        _highContrast = highContrast ?? throw new ArgumentNullException(nameof(highContrast));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _log = (log ?? throw new ArgumentNullException(nameof(log))).ForContext<ThemeService>();
        IsHighContrast = _highContrast.IsOn;
        _highContrast.Changed += OnHighContrastSignal;
    }

    /// <summary>Raised on the UI thread after the theme setting or high contrast changed.</summary>
    public event EventHandler? ThemeChanged;

    public bool IsHighContrast { get; private set; }

    public AppTheme Theme
    {
        get => _theme;
        set
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown theme.");
            }

            if (value == _theme)
            {
                return;
            }

            _theme = value;
            _log.Information("App theme set to {Theme}", value);
            foreach (var target in _targets.ToArray())
            {
                target.ApplyTheme(value);
            }

            ThemeChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public static ElementTheme ToElementTheme(AppTheme theme) => theme switch
    {
        AppTheme.Light => ElementTheme.Light,
        AppTheme.Dark => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    /// <summary>Applies the current theme now and on every later change, until <see cref="Unregister"/>.</summary>
    public void Register(IThemeTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!_targets.Contains(target))
        {
            _targets.Add(target);
        }

        target.ApplyTheme(_theme);
    }

    public void Unregister(IThemeTarget target) => _targets.Remove(target);

    public void Dispose() => _highContrast.Changed -= OnHighContrastSignal;

    private void OnHighContrastSignal(object? sender, EventArgs e) => _ui.Post(() =>
    {
        var on = _highContrast.IsOn;
        if (on == IsHighContrast)
        {
            return;
        }

        IsHighContrast = on;
        _log.Information("High contrast {State}", on ? "on" : "off");
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    });
}

/// <summary>
/// <see cref="IHighContrastSignal"/> for a desktop app. <c>AccessibilitySettings.HighContrastChanged</c>
/// can't be used: it needs a CoreWindow and throws 0x80070490 in a Win32 process (found in T2.2).
/// <c>UISettings.ColorValuesChanged</c> is raised on high-contrast changes and works here; the state
/// itself comes from <c>SystemParametersInfo(SPI_GETHIGHCONTRAST)</c>.
/// </summary>
internal sealed partial class SystemHighContrastSignal : IHighContrastSignal
{
    private const uint SpiGetHighContrast = 0x0042;
    private const uint HcfHighContrastOn = 0x1;

    private readonly Windows.UI.ViewManagement.UISettings? _uiSettings;

    public SystemHighContrastSignal(ILogger log)
    {
        ArgumentNullException.ThrowIfNull(log);
        try
        {
            _uiSettings = new Windows.UI.ViewManagement.UISettings();
            _uiSettings.ColorValuesChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        }
#pragma warning disable CA1031 // A missing signal only means HC changes need a window reopen; never fail startup.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            log.ForContext<SystemHighContrastSignal>().Warning(ex, "No colour-change notifications; high contrast changes apply to new windows only");
        }
    }

    public bool IsOn
    {
        get
        {
            var hc = new HighContrast { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<HighContrast>() };
            return SystemParametersInfo(SpiGetHighContrast, hc.Size, ref hc, 0) && (hc.Flags & HcfHighContrastOn) != 0;
        }
    }

    public event EventHandler? Changed;

    [System.Runtime.InteropServices.LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool SystemParametersInfo(uint action, uint param, ref HighContrast value, uint winIni);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct HighContrast
    {
        public uint Size;
        public uint Flags;
        public nint DefaultScheme;
    }
}
