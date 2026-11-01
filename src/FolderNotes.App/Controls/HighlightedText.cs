using FolderNotes.App.Search;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;

namespace FolderNotes.App.Controls;

/// <summary>One line of text with bold ranges (search highlights, specs/05 § Search palette behaviour).</summary>
public sealed partial class HighlightedText : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(HighlightedText), new PropertyMetadata(string.Empty, OnChanged));

    public static readonly DependencyProperty RangesProperty = DependencyProperty.Register(
        nameof(Ranges), typeof(IReadOnlyList<HighlightRange>), typeof(HighlightedText), new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty TextStyleProperty = DependencyProperty.Register(
        nameof(TextStyle), typeof(Style), typeof(HighlightedText), new PropertyMetadata(null, OnChanged));

    private readonly TextBlock _block = new() { TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 };

    public HighlightedText()
    {
        IsTabStop = false;
        Content = _block;
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public IReadOnlyList<HighlightRange>? Ranges
    {
        get => (IReadOnlyList<HighlightRange>?)GetValue(RangesProperty);
        set => SetValue(RangesProperty, value);
    }

    public Style? TextStyle
    {
        get => (Style?)GetValue(TextStyleProperty);
        set => SetValue(TextStyleProperty, value);
    }

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((HighlightedText)d).Render();

    private void Render()
    {
        if (TextStyle is { } style)
        {
            _block.Style = style;
        }

        _block.Inlines.Clear();
        var text = Text ?? "";
        var at = 0;
        foreach (var range in (Ranges ?? []).Where(r => r.Start >= 0 && r.Start + r.Length <= text.Length).OrderBy(r => r.Start))
        {
            if (range.Start < at)
            {
                continue;
            }

            if (range.Start > at)
            {
                _block.Inlines.Add(new Run { Text = text[at..range.Start] });
            }

            _block.Inlines.Add(new Run { Text = text.Substring(range.Start, range.Length), FontWeight = FontWeights.Bold });
            at = range.Start + range.Length;
        }

        if (at < text.Length)
        {
            _block.Inlines.Add(new Run { Text = text[at..] });
        }

        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this, text);
    }
}
