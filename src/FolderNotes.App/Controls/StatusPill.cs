using FolderNotes.App.Themes;
using FolderNotes.Core.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FolderNotes.App.Controls;

/// <summary>
/// A status pill: icon + text on the status tint (specs/05 § Visual language). Collapsed for
/// <see cref="NoteStatus.None"/>. UIA name "Status: <i>text</i>".
/// </summary>
public sealed partial class StatusPill : TokenControl
{
    public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(
        nameof(Status), typeof(NoteStatus), typeof(StatusPill), new PropertyMetadata(NoteStatus.None, OnStateChanged));

    private readonly Border _border = new();
    private readonly PathIcon _icon = new() { Width = 20, Height = 20 };
    private readonly TextBlock _text = new() { VerticalAlignment = VerticalAlignment.Center };

    public StatusPill()
    {
        _text.Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"];
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        // The geometry is 20 px; scale it to the caption line height.
        panel.Children.Add(new Viewbox { Width = 14, Height = 14, Child = _icon, VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(_text);
        _border.Child = panel;
        Content = _border;
        Visibility = Visibility.Collapsed;
    }

    public NoteStatus Status
    {
        get => (NoteStatus)GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    protected override void Render()
    {
        var visual = StatusVisuals.For(Status.IsDefined() ? Status : NoteStatus.None);
        Visibility = visual.HasPill ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(this, "Status: " + visual.Text);
        if (!visual.HasPill)
        {
            return;
        }

        _text.Text = visual.Text;
        _icon.Data = ThemeTokens.GetIconGeometry(visual.IconKey!);
        ChipChrome.Apply(this, _border, _text, visual.TintKey!);
        _icon.Foreground = _text.Foreground;
    }
}
