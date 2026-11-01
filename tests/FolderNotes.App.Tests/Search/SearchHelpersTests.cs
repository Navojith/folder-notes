using FolderNotes.App.Search;
using FolderNotes.Core.Search;

namespace FolderNotes.App.Tests.Search;

/// <summary>specs/05 § Search palette behaviour: highlighting and autocomplete.</summary>
public sealed class SearchHelpersTests
{
    private static SearchTerm[] Terms(params string[] t) => [.. t.Select(x => new SearchTerm(x, x.Contains(' ', StringComparison.Ordinal)))];

    private static string Marked(string text, IEnumerable<HighlightRange> ranges)
    {
        var s = text;
        foreach (var r in ranges.OrderByDescending(r => r.Start))
        {
            s = s[..r.Start] + "[" + s.Substring(r.Start, r.Length) + "]" + s[(r.Start + r.Length)..];
        }

        return s;
    }

    [Theory]
    [InlineData("Client: Acme Ltd", "ac", "Client: [Ac]me Ltd")]
    [InlineData("Client: Acme Ltd", "CLI", "[Cli]ent: Acme Ltd")]
    [InlineData("Café Müller", "cafe", "[Café] Müller")]
    [InlineData("Café Müller", "mul", "Café [Mül]ler")]
    [InlineData(@"D:\Clients\Acme Ltd", "acme", @"D:\Clients\[Acme] Ltd")]
    [InlineData("Reacme", "acme", "Reacme")] // word starts only
    public void REQ051_Highlight_WordPrefixes(string text, string term, string expected) =>
        Marked(text, SearchHighlight.Ranges(text, Terms(term))).ShouldBe(expected);

    [Fact]
    public void REQ051_Highlight_PhraseWords_AndSeveralTerms()
    {
        Marked("Acme invoices 2025", SearchHighlight.Ranges("Acme invoices 2025", Terms("acme inv", "20")))
            .ShouldBe("[Acme] [inv]oices [20]25");
        SearchHighlight.Ranges("Acme", Terms("acme", "ac")).ShouldBe([new HighlightRange(0, 4)]); // longest wins
        SearchHighlight.Ranges("Acme", []).ShouldBeEmpty();
    }

    [Fact]
    public void REQ052_Completion_Labels_PrefixAndQuoting()
    {
        var labels = new[] { "client", "do not touch", "Clients-old", "personal" };
        var s = SearchCompletion.Suggest("acme label:cl", 13, labels);
        s.Select(x => x.Display).ShouldBe(["client", "Clients-old"]);
        s[0].NewText.ShouldBe("acme label:client ");
        s[0].NewCaret.ShouldBe(18);

        var quoted = SearchCompletion.Suggest("label:\"do", 9, labels).Single();
        quoted.NewText.ShouldBe("label:\"do not touch\" ");
        SearchCompletion.Suggest("LABEL:", 6, labels).Count.ShouldBe(4); // empty value: all, key case ignored
    }

    [Fact]
    public void REQ052_Completion_StatusAndDue()
    {
        SearchCompletion.Suggest("status:s", 8, []).Select(x => x.Display).ShouldBe(["safe-to-delete"]);
        SearchCompletion.Suggest("status:", 7, []).Count.ShouldBe(6);
        SearchCompletion.Suggest("due:", 4, []).Select(x => x.Display).ShouldBe(["overdue", "7d", "30d", "90d"]);
        SearchCompletion.Suggest("due:3", 5, []).Single().NewText.ShouldBe("due:30d ");
    }

    [Fact]
    public void REQ052_Completion_OnlyForTheTokenAtTheCaret()
    {
        SearchCompletion.Suggest("label:cl acme", 13, ["client"]).ShouldBeEmpty(); // caret after "acme"
        var mid = SearchCompletion.Suggest("label:cl acme", 8, ["client"]).Single();
        mid.NewText.ShouldBe("label:client acme"); // the following space is reused
        mid.NewCaret.ShouldBe(13);
        SearchCompletion.Suggest("path:D:\\", 8, ["client"]).ShouldBeEmpty();
        SearchCompletion.Suggest("label:\"do not touch\"", 20, ["do not touch"]).ShouldBeEmpty(); // complete
        SearchCompletion.Suggest("acme", 4, ["acme"]).ShouldBeEmpty();
    }

    [Fact]
    public void REQ052_Completion_AtMostEight()
    {
        var many = Enumerable.Range(0, 20).Select(i => $"tag{i:00}");
        SearchCompletion.Suggest("label:tag", 9, many).Count.ShouldBe(SearchCompletion.MaxSuggestions);
    }
}
