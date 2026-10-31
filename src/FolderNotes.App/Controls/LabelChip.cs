using FolderNotes.App.Themes;
using FolderNotes.Core.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FolderNotes.App.Controls;

/// <summary>A label chip: the name on its palette colour (specs/05 § Design tokens). UIA name "Label: <i>name</i>".</summary>
public sealed partial class LabelChip : TokenControl
{
    public static readonly DependencyProperty LabelNameProperty = DependencyProperty.Register(
        nameof(LabelName), typeof(string), typeof(LabelChip), new PropertyMetadata(string.Empty, OnStateChanged));

    public static readonly DependencyProperty ColorKeyProperty = DependencyProperty.Register(
        nameof(ColorKey), typeof(string), typeof(LabelChip), new PropertyMetadata("blue", OnStateChanged));

    private readonly Border _border = new();
    private readonly TextBlock _text = new() { TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 };

    public LabelChip()
    {
        _text.Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"];
        _border.Child = _text;
        Content = _border;
    }

    public string LabelName
    {
        get => (string)GetValue(LabelNameProperty);
        set => SetValue(LabelNameProperty, value);
    }

    /// <summary>A key from <see cref="Palette.Keys"/>; anything else renders as blue.</summary>
    public string ColorKey
    {
        get => (string)GetValue(ColorKeyProperty);
        set => SetValue(ColorKeyProperty, value);
    }

    protected override void Render()
    {
        var key = Palette.IsValid(ColorKey) ? ColorKey : "blue";
        _text.Text = LabelName;
        AutomationProperties.SetName(this, "Label: " + LabelName);
        ChipChrome.Apply(this, _border, _text, key);
    }
}

/// <summary>The shared look of chips and pills.</summary>
internal static class ChipChrome
{
    public static void Apply(FrameworkElement owner, Border border, TextBlock text, string paletteKey)
    {
        border.Background = ThemeTokens.GetThemed<Brush>(owner, ThemeTokens.LabelChipBackgroundBrush(paletteKey));
        border.BorderBrush = ThemeTokens.GetThemed<Brush>(owner, "ChipBorderBrush");
        border.BorderThickness = ThemeTokens.GetThemed<object>(owner, "ChipBorderThickness") is Thickness t ? t : default;
        border.CornerRadius = ThemeTokens.Get<CornerRadius>("ChipCornerRadius");
        border.Padding = ThemeTokens.Get<Thickness>("ChipPadding");
        text.Foreground = ThemeTokens.GetThemed<Brush>(owner, ThemeTokens.LabelChipForegroundBrush(paletteKey));
    }
}
