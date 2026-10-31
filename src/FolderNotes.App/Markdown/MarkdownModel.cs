using System.Text;
using Markdig;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace FolderNotes.App.Markdown;

[Flags]
public enum SpanStyle
{
    None = 0,
    Bold = 1,
    Italic = 2,
    Code = 4,
    Strike = 8,
}

/// <summary>A run of text. <see cref="Url"/> is set only for links that are safe to open (http, https, mailto).</summary>
public sealed record MdSpan(string Text, SpanStyle Style = SpanStyle.None, string? Url = null)
{
    /// <summary>A line break inside a block.</summary>
    public static MdSpan Break { get; } = new("\n");
}

public enum MdBlockKind
{
    Heading,
    Paragraph,
    ListItem,
    Code,
    Rule,
}

/// <summary>
/// One block of the REQ-007 subset (specs/02 § Markdown).
/// </summary>
/// <param name="Kind">What it is.</param>
/// <param name="Spans">Inline content (empty for code and rules).</param>
/// <param name="Level">Heading level 1–6, or list depth (1 = top level). For other blocks inside a list item, that item's depth.</param>
/// <param name="Marker">List marker: "1." for ordered items, a bullet for the rest; null for task items and other blocks.</param>
/// <param name="Checked">For task list items: whether the box is ticked.</param>
/// <param name="QuoteDepth">How many block quotes it's inside.</param>
/// <param name="Code">The text of a code block.</param>
public sealed record MdBlock(
    MdBlockKind Kind,
    IReadOnlyList<MdSpan> Spans,
    int Level = 0,
    string? Marker = null,
    bool? Checked = null,
    int QuoteDepth = 0,
    string? Code = null);

/// <summary>Parses note Markdown into <see cref="MdBlock"/>s with Markdig (REQ-007, ADR-014). Pure; no XAML.</summary>
public static class MarkdownModel
{
    private static readonly string[] s_bullets = ["•", "◦", "▪"];

    private static readonly MarkdownPipeline s_pipeline = new MarkdownPipelineBuilder()
        .UseTaskLists()
        .UseAutoLinks()
        .UseEmphasisExtras(Markdig.Extensions.EmphasisExtras.EmphasisExtraOptions.Strikethrough)
        .DisableHtml()
        .Build();

    public static IReadOnlyList<MdBlock> Parse(string? markdown)
    {
        var blocks = new List<MdBlock>();
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return blocks;
        }

        var doc = Markdig.Markdown.Parse(markdown, s_pipeline);
        foreach (var block in doc)
        {
            AddBlock(blocks, block, listDepth: 0, quoteDepth: 0);
        }

        return blocks;
    }

    /// <summary>Only these links are clickable; everything else renders as plain text (02 § Markdown).</summary>
    public static bool IsSafeUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeMailto);

    private static void AddBlock(List<MdBlock> blocks, Block block, int listDepth, int quoteDepth)
    {
        switch (block)
        {
            case HeadingBlock heading:
                blocks.Add(new MdBlock(MdBlockKind.Heading, Spans(heading.Inline), heading.Level, QuoteDepth: quoteDepth));
                break;
            case ParagraphBlock paragraph:
                blocks.Add(new MdBlock(MdBlockKind.Paragraph, Spans(paragraph.Inline), listDepth, QuoteDepth: quoteDepth));
                break;
            case CodeBlock code: // fenced and indented
                blocks.Add(new MdBlock(MdBlockKind.Code, [], listDepth, QuoteDepth: quoteDepth, Code: CodeText(code)));
                break;
            case ThematicBreakBlock:
                blocks.Add(new MdBlock(MdBlockKind.Rule, [], listDepth, QuoteDepth: quoteDepth));
                break;
            case QuoteBlock quote:
                foreach (var child in quote)
                {
                    AddBlock(blocks, child, listDepth, quoteDepth + 1);
                }

                break;
            case ListBlock list:
                AddList(blocks, list, listDepth + 1, quoteDepth);
                break;
            case ContainerBlock container:
                foreach (var child in container)
                {
                    AddBlock(blocks, child, listDepth, quoteDepth);
                }

                break;
            case LeafBlock leaf:
                // Anything else (e.g. HTML when disabled): show its source text.
                var text = leaf.Lines.ToString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    blocks.Add(new MdBlock(MdBlockKind.Paragraph, [new MdSpan(text.TrimEnd())], listDepth, QuoteDepth: quoteDepth));
                }

                break;
        }
    }

    private static void AddList(List<MdBlock> blocks, ListBlock list, int depth, int quoteDepth)
    {
        var number = list.IsOrdered && int.TryParse(list.OrderedStart, out var start) ? start : 1;
        foreach (var item in list.OfType<ListItemBlock>())
        {
            var first = true;
            foreach (var child in item)
            {
                if (first && child is ParagraphBlock paragraph)
                {
                    var spans = Spans(paragraph.Inline, out var task);
                    var marker = task is not null ? null
                        : list.IsOrdered ? $"{number}{(list.OrderedDelimiter == ')' ? ')' : '.')}"
                        : s_bullets[Math.Min(depth, s_bullets.Length) - 1];
                    blocks.Add(new MdBlock(MdBlockKind.ListItem, spans, depth, marker, task, quoteDepth));
                }
                else
                {
                    if (first)
                    {
                        // An item that starts with something other than text still gets its marker.
                        blocks.Add(new MdBlock(MdBlockKind.ListItem, [], depth, list.IsOrdered ? $"{number}." : s_bullets[Math.Min(depth, s_bullets.Length) - 1], QuoteDepth: quoteDepth));
                    }

                    AddBlock(blocks, child, depth, quoteDepth);
                }

                first = false;
            }

            number++;
        }
    }

    private static string CodeText(CodeBlock code)
    {
        var sb = new StringBuilder();
        var lines = code.Lines.Lines;
        for (var i = 0; i < code.Lines.Count; i++)
        {
            if (i > 0)
            {
                sb.Append('\n');
            }

            sb.Append(lines[i].Slice.ToString());
        }

        return sb.ToString();
    }

    private static List<MdSpan> Spans(ContainerInline? inline) => Spans(inline, out _);

    private static List<MdSpan> Spans(ContainerInline? inline, out bool? task)
    {
        task = null;
        var spans = new List<MdSpan>();
        if (inline is null)
        {
            return spans;
        }

        foreach (var child in inline)
        {
            if (child is TaskList t && spans.Count == 0 && task is null)
            {
                task = t.Checked;
                continue;
            }

            AddInline(spans, child, SpanStyle.None, url: null);
        }

        // A task item's text starts after "[ ] "; drop the leading space.
        if (task is not null && spans.Count > 0 && spans[0].Text.StartsWith(' '))
        {
            spans[0] = spans[0] with { Text = spans[0].Text.TrimStart() };
        }

        return Merge(spans);
    }

    private static void AddInline(List<MdSpan> spans, Inline inline, SpanStyle style, string? url)
    {
        switch (inline)
        {
            case LiteralInline literal:
                spans.Add(new MdSpan(literal.Content.ToString(), style, url));
                break;
            case CodeInline code:
                spans.Add(new MdSpan(code.Content, style | SpanStyle.Code, url));
                break;
            case LineBreakInline lineBreak:
                spans.Add(lineBreak.IsHard ? MdSpan.Break : new MdSpan(" ", style, url));
                break;
            case AutolinkInline auto:
                var autoUrl = auto.IsEmail ? "mailto:" + auto.Url : auto.Url;
                spans.Add(new MdSpan(auto.Url, style, IsSafeUrl(autoUrl) ? autoUrl : null));
                break;
            case LinkInline { IsImage: true } image:
                spans.Add(new MdSpan(PlainText(image) is { Length: > 0 } alt ? alt : image.Url ?? "", style | SpanStyle.Italic, url));
                break;
            case LinkInline link:
                var target = IsSafeUrl(link.Url) ? link.Url : null;
                var before = spans.Count;
                foreach (var child in link)
                {
                    AddInline(spans, child, style, target);
                }

                if (spans.Count == before)
                {
                    spans.Add(new MdSpan(link.Url ?? "", style, target));
                }

                break;
            case EmphasisInline emphasis:
                var add = emphasis.DelimiterChar == '~' ? SpanStyle.Strike
                    : emphasis.DelimiterCount >= 2 ? SpanStyle.Bold
                    : SpanStyle.Italic;
                foreach (var child in emphasis)
                {
                    AddInline(spans, child, style | add, url);
                }

                break;
            case HtmlEntityInline entity:
                spans.Add(new MdSpan(entity.Transcoded.ToString(), style, url));
                break;
            case ContainerInline container:
                foreach (var child in container)
                {
                    AddInline(spans, child, style, url);
                }

                break;
            default:
                var text = inline.ToString();
                if (!string.IsNullOrEmpty(text))
                {
                    spans.Add(new MdSpan(text, style, url));
                }

                break;
        }
    }

    private static string PlainText(ContainerInline container)
    {
        var sb = new StringBuilder();
        foreach (var d in container.Descendants<LiteralInline>())
        {
            sb.Append(d.Content.ToString());
        }

        return sb.ToString();
    }

    /// <summary>Joins neighbouring spans with the same style and link, so the renderer makes fewer runs.</summary>
    private static List<MdSpan> Merge(List<MdSpan> spans)
    {
        var result = new List<MdSpan>(spans.Count);
        foreach (var span in spans)
        {
            if (span.Text.Length == 0)
            {
                continue;
            }

            if (result.Count > 0 && span != MdSpan.Break && result[^1] != MdSpan.Break
                && result[^1].Style == span.Style && result[^1].Url == span.Url)
            {
                result[^1] = result[^1] with { Text = result[^1].Text + span.Text };
            }
            else
            {
                result.Add(span);
            }
        }

        return result;
    }
}
