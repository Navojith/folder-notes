using FolderNotes.App.Themes;
using FolderNotes.Core.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FolderNotes.App.Controls;

/// <summary>
/// The note's accent bar (specs/05 § Library behaviour): a rounded strip in the palette accent
/// colour, or empty (but still taking its space) when the note has none.
/// </summary>
public sealed partial class AccentBar : TokenControl
{
    public static readonly DependencyProperty ColorKeyProperty = DependencyProperty.Register(
        nameof(ColorKey), typeof(string), typeof(AccentBar), new PropertyMetadata(string.Empty, OnStateChanged));

    private readonly Border _bar = new() { CornerRadius = new CornerRadius(2) };

    public AccentBar()
    {
        Width = ThemeTokens.Get<double>("AccentBarWidth");
        Content = _bar;
    }

    /// <summary>A palette key, or "" for no accent.</summary>
    public string ColorKey
    {
        get => (string)GetValue(ColorKeyProperty);
        set => SetValue(ColorKeyProperty, value);
    }

    protected override void Render() =>
        _bar.Background = Palette.IsValid(ColorKey)
            ? ThemeTokens.GetThemed<Brush>(this, ThemeTokens.LabelAccentBrush(ColorKey))
            : null;
}
