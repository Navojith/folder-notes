using FolderNotes.App.Themes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FolderNotes.App.Controls;

/// <summary>
/// Base for small controls whose colours come from run-time token keys (specs/05 § Design tokens).
/// Re-renders when the element's theme, the app theme or high contrast changes, while loaded.
/// </summary>
public abstract partial class TokenControl : UserControl
{
    protected TokenControl()
    {
        IsTabStop = false;
        Loaded += (_, _) =>
        {
            if (ThemeTokens.Service is { } service)
            {
                service.ThemeChanged += OnThemeChanged;
            }

            Render();
        };
        Unloaded += (_, _) =>
        {
            if (ThemeTokens.Service is { } service)
            {
                service.ThemeChanged -= OnThemeChanged;
            }
        };
        ActualThemeChanged += (_, _) => Render();
    }

    /// <summary>Rebuilds content and brushes from the current state and theme.</summary>
    protected abstract void Render();

    protected static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((TokenControl)d).Render();

    private void OnThemeChanged(object? sender, EventArgs e) => Render();
}
