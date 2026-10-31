using FolderNotes.Core.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace FolderNotes.App.Themes;

/// <summary>
/// Token keys from Themes/Tokens.xaml and lookups for keys built at run time, such as a label's
/// colour (specs/05 § Design tokens, ADR-013). XAML with a fixed key uses <c>{ThemeResource}</c> instead.
/// </summary>
internal static class ThemeTokens
{
    public const string TokensSource = "ms-appx:///Themes/Tokens.xaml";

    private static ResourceDictionary? s_tokens;

    /// <summary>Set once at startup; supplies high-contrast state and change notifications.</summary>
    public static ThemeService? Service { get; set; }

    public static string LabelAccent(string paletteKey) => Key("LabelAccent", paletteKey);

    public static string LabelAccentBrush(string paletteKey) => Key("LabelAccentBrush", paletteKey);

    public static string LabelChipBackground(string paletteKey) => Key("LabelChipBackground", paletteKey);

    public static string LabelChipBackgroundBrush(string paletteKey) => Key("LabelChipBackgroundBrush", paletteKey);

    public static string LabelChipForeground(string paletteKey) => Key("LabelChipForeground", paletteKey);

    public static string LabelChipForegroundBrush(string paletteKey) => Key("LabelChipForegroundBrush", paletteKey);

    /// <summary>The theme dictionary name to use for <paramref name="element"/>: Light, Dark or HighContrast.</summary>
    public static string ThemeKey(FrameworkElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return Service?.IsHighContrast == true ? "HighContrast"
            : element.ActualTheme == ElementTheme.Dark ? "Dark"
            : "Light";
    }

    /// <summary>A themed token as it applies to <paramref name="element"/> right now, or null if missing.</summary>
    public static T? GetThemed<T>(FrameworkElement element, string key)
        where T : class
    {
        var tokens = Tokens();
        return tokens is not null
            && tokens.ThemeDictionaries.TryGetValue(ThemeKey(element), out var d)
            && d is ResourceDictionary theme
            && theme.TryGetValue(key, out var value)
            ? value as T
            : null;
    }

    /// <summary>A theme-independent token (spacing, icon geometry…), or null if missing.</summary>
    public static T? Get<T>(string key)
    {
        var tokens = Tokens();
        return tokens is not null && tokens.TryGetValue(key, out var value) && value is T t ? t : default;
    }

    /// <summary>Icon geometry for <paramref name="iconKey"/> (a <c>StatusIcon.*</c> key), parsed fresh: a Geometry can have only one parent.</summary>
    public static Geometry? GetIconGeometry(string iconKey)
    {
        var data = Get<string>(iconKey);
        return data is null ? null : (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), data);
    }

    private static string Key(string token, string paletteKey) =>
        Palette.IsValid(paletteKey)
            ? token + "." + paletteKey
            : throw new ArgumentException($"'{paletteKey}' is not a palette key.", nameof(paletteKey));

    private static ResourceDictionary? Tokens()
    {
        if (s_tokens is not null)
        {
            return s_tokens;
        }

        foreach (var d in Application.Current.Resources.MergedDictionaries)
        {
            if (d.Source?.OriginalString == TokensSource)
            {
                return s_tokens = d;
            }
        }

        return null;
    }
}
