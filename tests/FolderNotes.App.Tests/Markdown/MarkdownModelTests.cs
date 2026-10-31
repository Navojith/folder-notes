using FolderNotes.App.Markdown;

namespace FolderNotes.App.Tests.Markdown;

/// <summary>REQ-007: the Markdown subset (headings, bold, italic, lists, checkboxes, links, inline code).</summary>
public sealed class MarkdownModelTests
{
    [Fact]
    public void REQ007_EmptyOrWhitespace_GivesNoBlocks()
    {
        MarkdownModel.Parse(null).ShouldBeEmpty();
        MarkdownModel.Parse("  \n\n ").ShouldBeEmpty();
    }

    [Fact]
    public void REQ007_Headings_KeepTheirLevel()
    {
        var blocks = MarkdownModel.Parse("# One\n\n### Three");
        blocks.Select(b => (b.Kind, b.Level, Text(b))).ShouldBe([(MdBlockKind.Heading, 1, "One"), (MdBlockKind.Heading, 3, "Three")]);
    }

    [Fact]
    public void REQ007_BoldItalicCodeStrike_BecomeStyles()
    {
        var spans = MarkdownModel.Parse("a **b** *c* `d` ~~e~~ ***f***").Single().Spans;
        spans.ShouldBe([
            new MdSpan("a "),
            new MdSpan("b", SpanStyle.Bold),
            new MdSpan(" "),
            new MdSpan("c", SpanStyle.Italic),
            new MdSpan(" "),
            new MdSpan("d", SpanStyle.Code),
            new MdSpan(" "),
            new MdSpan("e", SpanStyle.Strike),
            new MdSpan(" "),
            new MdSpan("f", SpanStyle.Bold | SpanStyle.Italic),
        ]);
    }

    [Fact]
    public void REQ007_Lists_HaveMarkersAndDepth()
    {
        var blocks = MarkdownModel.Parse("- one\n  - nested\n- two\n\n3. three\n4. four");
        blocks.Select(b => (b.Kind, b.Level, b.Marker, Text(b))).ShouldBe([
            (MdBlockKind.ListItem, 1, "•", "one"),
            (MdBlockKind.ListItem, 2, "◦", "nested"),
            (MdBlockKind.ListItem, 1, "•", "two"),
            (MdBlockKind.ListItem, 1, "3.", "three"),
            (MdBlockKind.ListItem, 1, "4.", "four"),
        ]);
    }

    [Fact]
    public void REQ007_TaskItems_HaveACheckAndNoMarker()
    {
        var blocks = MarkdownModel.Parse("- [ ] Upload 2026 invoices\n- [x] Sign SOW");
        blocks.Select(b => (b.Marker, b.Checked, Text(b))).ShouldBe([
            ((string?)null, (bool?)false, "Upload 2026 invoices"),
            (null, true, "Sign SOW"),
        ]);
    }

    [Fact]
    public void REQ007_Links_KeepSafeUrlsOnly()
    {
        var spans = MarkdownModel.Parse("[site](https://example.com) [bad](javascript:alert(1)) [file](file:///C:/x) <mailto:sam@example.com>").Single().Spans;
        spans.ShouldContain(new MdSpan("site", SpanStyle.None, "https://example.com"));
        spans.Single(s => s.Text.Contains("bad", StringComparison.Ordinal)).Url.ShouldBeNull();
        spans.Single(s => s.Text.Contains("file", StringComparison.Ordinal)).Url.ShouldBeNull();
        spans.Where(s => s.Url is not null).Select(s => s.Url).ShouldBe(["https://example.com", "mailto:sam@example.com"]);
    }

    [Fact]
    public void REQ007_BareUrls_AreAutoLinked()
    {
        var spans = MarkdownModel.Parse("see https://example.com/a for more").Single().Spans;
        spans.ShouldContain(s => s.Url == "https://example.com/a");
    }

    [Fact]
    public void RawHtml_IsShownAsText_NotInterpreted()
    {
        var blocks = MarkdownModel.Parse("<b>hi</b> there\n\n<div>block</div>");
        string.Concat(blocks.Select(Text)).ShouldContain("<b>hi</b>");
        string.Concat(blocks.Select(Text)).ShouldContain("<div>block</div>");
        blocks.SelectMany(b => b.Spans).ShouldAllBe(s => s.Style == SpanStyle.None);
    }

    [Fact]
    public void CodeBlocksQuotesAndRules()
    {
        var blocks = MarkdownModel.Parse("```\nline 1\nline 2\n```\n\n> quoted\n\n---");
        blocks[0].ShouldBe(new MdBlock(MdBlockKind.Code, [], Code: "line 1\nline 2"), new MdBlockComparer());
        (blocks[1].Kind, blocks[1].QuoteDepth, Text(blocks[1])).ShouldBe((MdBlockKind.Paragraph, 1, "quoted"));
        blocks[2].Kind.ShouldBe(MdBlockKind.Rule);
    }

    [Fact]
    public void HardBreaks_AreBreakSpans_SoftBreaksAreSpaces()
    {
        MarkdownModel.Parse("a\nb").Single().Spans.ShouldBe([new MdSpan("a b")]);
        MarkdownModel.Parse("a  \nb").Single().Spans.ShouldBe([new MdSpan("a"), MdSpan.Break, new MdSpan("b")]);
    }

    [Theory]
    [InlineData("https://x.org", true)]
    [InlineData("http://x.org", true)]
    [InlineData("mailto:a@b.c", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("file:///C:/Windows", false)]
    [InlineData("ms-settings:", false)]
    [InlineData("relative/path", false)]
    [InlineData(null, false)]
    public void SafeUrls(string? url, bool safe) => MarkdownModel.IsSafeUrl(url).ShouldBe(safe);

    [Fact]
    public void FiftyThousandCharacters_ParseQuickly()
    {
        var body = string.Concat(Enumerable.Repeat("- [ ] **item** with `code` and [a link](https://x.org)\n", 900));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        MarkdownModel.Parse(body).Count.ShouldBe(900);
        sw.ElapsedMilliseconds.ShouldBeLessThan(500);
    }

    private static string Text(MdBlock b) => string.Concat(b.Spans.Select(s => s.Text));

    private sealed class MdBlockComparer : IEqualityComparer<MdBlock>
    {
        public bool Equals(MdBlock? x, MdBlock? y) =>
            x is not null && y is not null && x.Kind == y.Kind && x.Level == y.Level && x.Code == y.Code && x.Spans.SequenceEqual(y.Spans);

        public int GetHashCode(MdBlock obj) => obj.Kind.GetHashCode();
    }
}
