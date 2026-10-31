using FolderNotes.App.Themes;
using Microsoft.UI.Xaml.Controls;

namespace FolderNotes.App.Controls;

/// <summary>
/// Base for small controls whose colours come from run-time token keys (specs/05 § Design tokens).
/// Re-renders when the element's theme, the app theme or high contrast changes.
/// </summary>
/// <remarks>
/// The subscription to the process-lifetime <see cref="ThemeService"/> is weak: <c>Unloaded</c> isn't
/// raised when a window is destroyed, so a strong handler kept every closed editor's tree alive (T2.3).
/// </remarks>
public abstract partial class TokenControl : UserControl
{
    protected TokenControl()
    {
        IsTabStop = false;
        if (ThemeTokens.Service is { } service)
        {
            WeakThemeHandler.Subscribe(service, this);
        }

        Loaded += (_, _) => Render();
        ActualThemeChanged += (_, _) => Render();
    }

    /// <summary>Rebuilds content and brushes from the current state and theme.</summary>
    protected abstract void Render();

    protected static void OnStateChanged(Microsoft.UI.Xaml.DependencyObject d, Microsoft.UI.Xaml.DependencyPropertyChangedEventArgs e) =>
        ((TokenControl)d).Render();

    /// <summary>Holds the control weakly and unsubscribes itself once the control is collected.</summary>
    private sealed class WeakThemeHandler
    {
        private readonly WeakReference<TokenControl> _target;
        private readonly ThemeService _service;

        private WeakThemeHandler(ThemeService service, TokenControl target)
        {
            _service = service;
            _target = new WeakReference<TokenControl>(target);
        }

        public static void Subscribe(ThemeService service, TokenControl target) =>
            service.ThemeChanged += new WeakThemeHandler(service, target).OnThemeChanged;

        private void OnThemeChanged(object? sender, EventArgs e)
        {
            if (_target.TryGetTarget(out var control))
            {
                control.Render();
            }
            else
            {
                _service.ThemeChanged -= OnThemeChanged;
            }
        }
    }
}
