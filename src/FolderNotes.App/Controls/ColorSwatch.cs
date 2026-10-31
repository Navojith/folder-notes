using FolderNotes.App.Themes;
using FolderNotes.Core.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace FolderNotes.App.Controls;

/// <summary>A round colour dot for a palette key; a "⊘" in the inherited text colour for none (null key).</summary>
public sealed partial class ColorSwatch : TokenControl
{
    public static readonly DependencyProperty ColorKeyProperty = DependencyProperty.Register(
        nameof(ColorKey), typeof(string), typeof(ColorSwatch), new PropertyMetadata(null, OnStateChanged));

    private readonly Ellipse _dot = new() { Width = 18, Height = 18 };
    private readonly TextBlock _none = new() { Text = "⊘", FontSize = 18, LineHeight = 18, VerticalAlignment = VerticalAlignment.Center };

    public string? ColorKey
    {
        get => (string?)GetValue(ColorKeyProperty);
        set => SetValue(ColorKeyProperty, value);
    }

    protected override void Render()
    {
        if (Palette.IsValid(ColorKey))
        {
            _dot.Fill = ThemeTokens.GetThemed<Brush>(this, ThemeTokens.LabelAccentBrush(ColorKey!));
            Content = _dot;
        }
        else
        {
            Content = _none;
        }
    }
}
