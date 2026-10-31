using FolderNotes.Core.Domain;
using FolderNotes.Core.Search;
using FolderNotes.Core.Storage;

namespace FolderNotes.Core.Tests.Search;

public sealed class SearchQueryParserTests
{
    private static readonly DateOnly s_today = new(2026, 10, 31);

    private static ParsedQuery P(string q) => SearchQueryParser.Parse(q, s_today);

    [Fact]
    public void REQ051_PlainWords_AreTerms()
    {
        var q = P("  acme   invoices ");
        q.Terms.ShouldBe([new SearchTerm("acme", false), new SearchTerm("invoices", false)]);
        q.Errors.ShouldBeEmpty();
        q.Filters.Labels.ShouldBeEmpty();
        q.Filters.Statuses.ShouldBeEmpty();
        q.Filters.PathsUnder.ShouldBeEmpty();
        q.Filters.Due.ShouldBeNull();
        q.Filters.Keep.ShouldBeNull();
    }

    [Fact]
    public void REQ051_EmptyOrNull_HasNothing()
    {
        P("").HasTerms.ShouldBeFalse();
        SearchQueryParser.Parse(null, s_today).HasTerms.ShouldBeFalse();
    }

    [Fact]
    public void REQ051_QuotedWords_AreAPhrase()
    {
        var q = P("\"build output\" acme");
        q.Terms.ShouldBe([new SearchTerm("build output", true), new SearchTerm("acme", false)]);
    }

    [Fact]
    public void REQ052_Label_IsRepeatable()
    {
        var q = P("label:client label:\"do not touch\" acme");
        q.Filters.Labels.ShouldBe(["client", "do not touch"]);
        q.Terms.Single().Text.ShouldBe("acme");
    }

    [Theory]
    [InlineData("status:keep", NoteStatus.Keep)]
    [InlineData("STATUS:Safe-To-Delete", NoteStatus.SafeToDelete)]
    [InlineData("status:in-progress", NoteStatus.InProgress)]
    public void REQ052_Status_ParsesKeys(string token, NoteStatus expected) => P(token).Filters.Statuses.ShouldBe([expected]);

    [Fact]
    public void REQ052_Status_Repeated_MeansAnyOf() =>
        P("status:keep status:archive").Filters.Statuses.ShouldBe([NoteStatus.Keep, NoteStatus.Archive]);

    [Fact]
    public void REQ052_Status_Unknown_IsReportedAndIgnored()
    {
        var q = P("status:bogus acme");
        q.Filters.Statuses.ShouldBeEmpty();
        q.Errors.Single().Token.ShouldBe("status:bogus");
        q.Terms.Single().Text.ShouldBe("acme");
    }

    [Theory]
    [InlineData(@"path:D:\Projects", @"D:\PROJECTS")]
    [InlineData(@"path:d:/projects/", @"D:\PROJECTS")]
    [InlineData(@"path:""D:\My Projects""", @"D:\MY PROJECTS")]
    [InlineData(@"path:\\nas\share", @"\\NAS\SHARE")]
    public void REQ052_Path_IsNormalized(string token, string expected) => P(token).Filters.PathsUnder.ShouldBe([expected]);

    [Fact]
    public void REQ052_Path_Invalid_IsReported() => P(@"path:relative\dir").Errors.ShouldHaveSingleItem();

    [Fact]
    public void REQ051_UnknownKey_IsText() =>
        P(@"C:\Temp foo:bar").Terms.Select(t => t.Text).ShouldBe([@"C:\Temp", "foo:bar"]);

    [Fact]
    public void REQ051_EmptyFilterValue_IsText() => P("label:").Terms.Single().Text.ShouldBe("label:");

    [Fact]
    public void REQ023_DueOverdue_IsBeforeToday() =>
        P("due:overdue").Filters.Due.ShouldBe(new DateRange(null, new DateOnly(2026, 10, 30)));

    [Theory]
    [InlineData("due:30d", 2026, 11, 30)]
    [InlineData("due:2w", 2026, 11, 14)]
    [InlineData("due:1m", 2026, 11, 30)]
    [InlineData("due:1y", 2027, 10, 31)]
    [InlineData("due:0d", 2026, 10, 31)]
    public void REQ023_DueRelative_IsTodayThroughN(string token, int y, int m, int d) =>
        P(token).Filters.Due.ShouldBe(new DateRange(s_today, new DateOnly(y, m, d)));

    [Fact]
    public void REQ023_DueToday() => P("due:today").Filters.Due.ShouldBe(new DateRange(s_today, s_today));

    [Theory]
    [InlineData("keep:<2027", null, "2026-12-31")]
    [InlineData("keep:<=2027", null, "2027-12-31")]
    [InlineData("keep:>2027", "2028-01-01", null)]
    [InlineData("keep:>=2027", "2027-01-01", null)]
    [InlineData("keep:2027", "2027-01-01", "2027-12-31")]
    [InlineData("keep:2028-02", "2028-02-01", "2028-02-29")]
    [InlineData("keep:<2027-06", null, "2027-05-31")]
    [InlineData("keep:2027-06-15", "2027-06-15", "2027-06-15")]
    [InlineData("keep:>2027-06-15", "2027-06-16", null)]
    public void REQ023_KeepComparisons(string token, string? from, string? to) =>
        P(token).Filters.Keep.ShouldBe(new DateRange(from is null ? null : DateOnly.Parse(from, System.Globalization.CultureInfo.InvariantCulture),
                                                      to is null ? null : DateOnly.Parse(to, System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public void REQ023_DueAbsolute_AlsoWorks() =>
        P("due:<2027").Filters.Due.ShouldBe(new DateRange(null, new DateOnly(2026, 12, 31)));

    [Theory]
    [InlineData("due:soon")]
    [InlineData("due:13x")]
    [InlineData("keep:overdue")]
    [InlineData("keep:<27")]
    [InlineData("keep:2027-13")]
    [InlineData("keep:2027-02-30")]
    [InlineData("due:99999999d")]
    public void REQ023_BadDateFilter_IsReported(string token)
    {
        var q = P(token);
        q.Errors.ShouldHaveSingleItem().Token.ShouldBe(token);
        q.Filters.Due.ShouldBeNull();
        q.Filters.Keep.ShouldBeNull();
    }

    [Fact]
    public void REQ052_Everything_Combined()
    {
        var q = P("acme label:client status:keep path:D:\\Clients due:30d keep:<2027 \"tax docs\"");
        q.Terms.Select(t => t.Text).ShouldBe(["acme", "tax docs"]);
        q.Filters.Labels.ShouldBe(["client"]);
        q.Filters.Statuses.ShouldBe([NoteStatus.Keep]);
        q.Filters.PathsUnder.ShouldBe([@"D:\CLIENTS"]);
        q.Filters.Due.ShouldNotBeNull();
        q.Filters.Keep.ShouldNotBeNull();
        q.Errors.ShouldBeEmpty();
    }

    [Fact]
    public void REQ051_UnbalancedQuote_TakesTheRest() =>
        P("\"open phrase acme").Terms.ShouldBe([new SearchTerm("open phrase acme", true)]);

    [Theory]
    [InlineData("acme", "\"acme\"*")]
    [InlineData("say \"hi\"", "\"say\"* \"hi\"*")]
    public void REQ051_FtsQuery_QuotesEveryTerm(string raw, string expected) =>
        SearchService.ToFtsQuery(P(raw).Terms).ShouldBe(expected);

    [Fact]
    public void REQ051_FtsQuery_EscapesEmbeddedQuotes() =>
        SearchService.ToFtsQuery([new SearchTerm("a\"b", false)]).ShouldBe("\"a\"\"b\"*");

    [Theory]
    [InlineData("acme", "acme", 1, 0)]
    [InlineData("acme", "acne", 1, 1)]
    [InlineData("invoice", "invocie", 1, 1)] // transposition
    [InlineData("retention", "retnetion", 2, 1)]
    [InlineData("abc", "xyz", 1, 2)]
    public void REQ051_Damerau(string a, string b, int max, int expected) => SearchService.Damerau(a, b, max).ShouldBe(expected);

    [Fact]
    public void REQ051_Words_FoldCaseAndDiacritics() =>
        SearchService.Words("Données Été-2026 / Ärzte").ShouldBe(["donnees", "ete", "2026", "arzte"]);
}
