using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace FolderNotes.App.Markdown;

/// <summary>Renders <see cref="MarkdownModel"/> blocks into a selectable <see cref="RichTextBlock"/> (specs/02 § Markdown).</summary>
public sealed partial class MarkdownView : UserControl
{
    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
        nameof(Markdown), typeof(string), typeof(MarkdownView), new PropertyMetadata(string.Empty, (d, _) => ((MarkdownView)d).Render()));

    private static readonly FontFamily s_mono = new("Cascadia Mono, Consolas");
    private readonly RichTextBlock _text = new() { IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap };

    public MarkdownView()
    {
        IsTabStop = false;
        Content = _text;
    }

    public string Markdown
    {
        get => (string)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    /// <summary>Shown when the body is empty.</summary>
    public string EmptyText { get; set; } = "Nothing to preview.";

    private void Render()
    {
        _text.Blocks.Clear();
        var blocks = MarkdownModel.Parse(Markdown);
        if (blocks.Count == 0)
        {
            var empty = new Paragraph();
            empty.Inlines.Add(new Run { Text = EmptyText, FontStyle = Windows.UI.Text.FontStyle.Italic });
            _text.Blocks.Add(empty);
            return;
        }

        foreach (var block in blocks)
        {
            _text.Blocks.Add(ToParagraph(block));
        }
    }

    private static Paragraph ToParagraph(MdBlock block)
    {
        var indent = (block.QuoteDepth * 16) + (Math.Max(0, block.Level - (block.Kind == MdBlockKind.Heading ? block.Level : 1)) * 20);
        var p = new Paragraph { Margin = new Thickness(indent, 0, 0, 8) };
        switch (block.Kind)
        {
            case MdBlockKind.Heading:
                p.FontSize = block.Level switch { 1 => 24, 2 => 20, _ => 16 };
                p.FontWeight = FontWeights.SemiBold;
                p.Margin = new Thickness(indent, block.Level <= 2 ? 8 : 4, 0, 6);
                break;
            case MdBlockKind.ListItem:
                p.Margin = new Thickness(indent + 4, 0, 0, 4);
                var marker = block.Checked switch { true => "☑", false => "☐", null => block.Marker ?? "•" };
                p.Inlines.Add(new Run { Text = marker + "  " });
                break;
            case MdBlockKind.Code:
                p.FontFamily = s_mono;
                p.Inlines.Add(new Run { Text = block.Code ?? "" });
                return p;
            case MdBlockKind.Rule:
                p.Inlines.Add(new Run { Text = new string('─', 24) });
                return p;
        }

        if (block.QuoteDepth > 0)
        {
            p.FontStyle = Windows.UI.Text.FontStyle.Italic;
        }

        foreach (var span in block.Spans)
        {
            p.Inlines.Add(ToInline(span));
        }

        return p;
    }

    private static Inline ToInline(MdSpan span)
    {
        if (ReferenceEquals(span, MdSpan.Break) || span == MdSpan.Break)
        {
            return new LineBreak();
        }

        var run = new Run { Text = span.Text };
        if (span.Style.HasFlag(SpanStyle.Bold))
        {
            run.FontWeight = FontWeights.SemiBold;
        }

        if (span.Style.HasFlag(SpanStyle.Italic))
        {
            run.FontStyle = Windows.UI.Text.FontStyle.Italic;
        }

        if (span.Style.HasFlag(SpanStyle.Code))
        {
            run.FontFamily = s_mono;
        }

        if (span.Style.HasFlag(SpanStyle.Strike))
        {
            run.TextDecorations = Windows.UI.Text.TextDecorations.Strikethrough;
        }

        if (span.Url is not null && Uri.TryCreate(span.Url, UriKind.Absolute, out var uri))
        {
            // MarkdownModel only lets http, https and mailto through; NavigateUri opens them in the default handler.
            var link = new Hyperlink { NavigateUri = uri };
            ToolTipService.SetToolTip(link, span.Url);
            link.Inlines.Add(run);
            return link;
        }

        return run;
    }
}
