using FolderNotes.Core.Domain;
using FolderNotes.Core.Search;
using FolderNotes.Core.Tests.Storage;

namespace FolderNotes.Core.Tests.Search;

public sealed class SearchServiceTests : IAsyncLifetime
{
    private StoreFixture _f = null!;
    private SearchService _search = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _f = await StoreFixture.CreateAsync();
        _search = new SearchService(_f.Store);
    }

    public async ValueTask DisposeAsync()
    {
        _search.Dispose();
        await _f.DisposeAsync();
    }

    private Task<Note> Add(string path, string title, string body = "", NoteStatus status = NoteStatus.None, params string[] labels) =>
        _f.Store.UpsertAsync(_f.NewNote(path, title) with
        {
            BodyMd = body,
            Status = status,
            Labels = [.. labels.Select(l => Label.Create(l, "blue"))],
        }, Ct);

    private async Task<string[]> Titles(string q, int limit = 20) => [.. (await _search.SearchAsync(q, limit, Ct)).Select(h => h.Note.Title)];

    [Fact]
    public async Task REQ051_MatchesTitleBodyLabelsAndPath()
    {
        await Add(@"C:\A", "Acme invoices");
        await Add(@"C:\B", "Body only", "talks about acme somewhere");
        await Add(@"C:\C", "Labelled", labels: "acme-corp");
        await Add(@"D:\Clients\Acme", "Path only");
        await Add(@"C:\E", "Unrelated");

        (await Titles("acme")).ShouldBe(["Acme invoices", "Body only", "Labelled", "Path only"], ignoreOrder: true);
    }

    [Fact]
    public async Task REQ051_Ranking_TitleBeatsLabelsBeatsPathBeatsBody()
    {
        await Add(@"C:\1", "Body", "zephyr");
        await Add(@"C:\zephyr", "Path");
        await Add(@"C:\3", "Label", labels: "zephyr");
        await Add(@"C:\4", "zephyr title");

        (await Titles("zephyr")).ShouldBe(["zephyr title", "Label", "Path", "Body"]);
    }

    [Fact]
    public async Task REQ051_PrefixMatching()
    {
        await Add(@"C:\A", "Retention policy");
        (await Titles("ret")).ShouldBe(["Retention policy"]);
        (await Titles("re")).ShouldBe(["Retention policy"]);
        (await Titles("r")).ShouldBe(["Retention policy"]);
    }

    [Fact]
    public async Task REQ051_AllTermsMustMatch()
    {
        await Add(@"C:\A", "Acme invoices 2025");
        await Add(@"C:\B", "Acme contracts");
        (await Titles("acme inv")).ShouldBe(["Acme invoices 2025"]);
    }

    [Fact]
    public async Task REQ051_Phrase()
    {
        await Add(@"C:\A", "build output folder");
        await Add(@"C:\B", "output of the build");
        (await Titles("\"build output\"")).ShouldBe(["build output folder"]);
    }

    [Fact]
    public async Task REQ051_DiacriticsAndCaseAreIgnored()
    {
        await Add(@"C:\A", "Données été");
        (await Titles("DONNEES")).ShouldBe(["Données été"]);
        (await Titles("ete")).ShouldBe(["Données été"]);
    }

    [Fact]
    public async Task REQ051_TypoFallback_FindsMisspelledTitlesAndLabels()
    {
        await Add(@"C:\A", "Invoices archive");
        await Add(@"C:\B", "Something else", labels: "photography");
        await Add(@"C:\C", "Unrelated");

        var hits = await _search.SearchAsync("invocies", 10, Ct);
        hits.Single().Note.Title.ShouldBe("Invoices archive");
        hits.Single().Kind.ShouldBe(SearchMatchKind.Fuzzy);

        (await Titles("photograhpy")).ShouldBe(["Something else"]);
        (await Titles("xyz")).ShouldBeEmpty(); // short terms never fuzz
    }

    [Fact]
    public async Task REQ051_FtsHitsComeBeforeFuzzyHits()
    {
        await Add(@"C:\A", "Report draft");
        await Add(@"C:\B", "Rpeort final"); // typo in the data

        var hits = await _search.SearchAsync("report", 10, Ct);
        hits.Select(h => (h.Note.Title, h.Kind)).ShouldBe([("Report draft", SearchMatchKind.FullText), ("Rpeort final", SearchMatchKind.Fuzzy)]);
    }

    [Fact]
    public async Task REQ052_FiltersCombineWithText()
    {
        await Add(@"D:\Clients\Acme", "Acme", status: NoteStatus.Keep, labels: "client");
        await Add(@"D:\Clients\Acme2", "Acme two", status: NoteStatus.Archive, labels: "client");
        await Add(@"E:\Other\Acme", "Acme other", status: NoteStatus.Keep);

        (await Titles("acme label:client")).ShouldBe(["Acme", "Acme two"], ignoreOrder: true);
        (await Titles("acme status:keep")).ShouldBe(["Acme", "Acme other"], ignoreOrder: true);
        (await Titles(@"acme path:D:\Clients status:keep")).ShouldBe(["Acme"]);
        (await Titles("acme label:nope")).ShouldBeEmpty();
    }

    [Fact]
    public async Task REQ052_FiltersAlone_ListNewestFirst()
    {
        await Add(@"C:\A", "first", status: NoteStatus.Review);
        _f.Clock.Advance(TimeSpan.FromMinutes(1));
        await Add(@"C:\B", "second", status: NoteStatus.Review);
        await Add(@"C:\C", "other");

        var hits = await _search.SearchAsync("status:review", 10, Ct);
        hits.Select(h => h.Note.Title).ShouldBe(["second", "first"]);
        hits.ShouldAllBe(h => h.Kind == SearchMatchKind.Filter);
    }

    [Fact]
    public async Task REQ023_DateFilters()
    {
        // Today is 2026-10-31 (fixture clock).
        await _f.Store.UpsertAsync(_f.NewNote(@"C:\Overdue", "overdue") with { ReviewOn = new DateOnly(2026, 10, 1) }, Ct);
        await _f.Store.UpsertAsync(_f.NewNote(@"C:\Soon", "soon") with { SafeAfter = new DateOnly(2026, 11, 20) }, Ct);
        await _f.Store.UpsertAsync(_f.NewNote(@"C:\Later", "later") with { ReviewOn = new DateOnly(2027, 3, 1) }, Ct);
        await _f.Store.UpsertAsync(_f.NewNote(@"C:\Keep", "keeper") with { KeepUntil = new DateOnly(2026, 12, 31) }, Ct);
        await _f.Store.UpsertAsync(_f.NewNote(@"C:\Keep2", "keeper long") with { KeepUntil = new DateOnly(2030, 1, 1) }, Ct);

        (await Titles("due:overdue")).ShouldBe(["overdue"]);
        (await Titles("due:30d")).ShouldBe(["soon"]);
        (await Titles("due:1y")).ShouldBe(["later", "soon"], ignoreOrder: true);
        (await Titles("keep:<2027")).ShouldBe(["keeper"]);
        (await Titles("keep:>2027")).ShouldBe(["keeper long"]);
        (await Titles("keeper keep:<2027")).ShouldBe(["keeper"]);
    }

    [Fact]
    public async Task REQ051_DeletedNotesAreNotFound_RestoredAreFoundAgain()
    {
        var n = await Add(@"C:\A", "Ephemeral");
        await _f.Store.DeleteAsync(n.Id, Ct);
        (await Titles("ephemeral")).ShouldBeEmpty();
        (await Titles("ephemerl")).ShouldBeEmpty(); // fuzzy cache was invalidated too
        await _f.Store.RestoreAsync(n.Id, Ct);
        (await Titles("ephemeral")).ShouldBe(["Ephemeral"]);
    }

    [Fact]
    public async Task REQ012_LabelRename_IsSearchable()
    {
        var n = await Add(@"C:\A", "Note", labels: "client");
        await _f.Store.UpdateLabelAsync(n.Labels.Single() with { Name = "customer" }, Ct);

        (await Titles("customer")).ShouldBe(["Note"]);
        (await Titles("label:customer")).ShouldBe(["Note"]);
        (await Titles("client")).ShouldBeEmpty();
    }

    [Fact]
    public async Task REQ061_RelinkedPath_IsSearchable()
    {
        var n = await Add(@"C:\Old\Place", "Wanderer");
        await _f.Store.RelinkAsync(n.Id, StoreFixture.Folder(@"E:\Newhome"), null, Ct);
        (await Titles("newhome")).ShouldBe(["Wanderer"]);
        (await Titles(@"path:E:\Newhome")).ShouldBe(["Wanderer"]);
    }

    [Theory]
    [InlineData("\"")]
    [InlineData("AND")]
    [InlineData("OR NOT")]
    [InlineData("NEAR(a b)")]
    [InlineData("title:x")]
    [InlineData("*")]
    [InlineData("^acme")]
    [InlineData("a\"b\"c")]
    public async Task REQ051_FtsSyntaxInInput_NeverThrows(string q)
    {
        await Add(@"C:\A", "Acme AND friends");
        await Should.NotThrowAsync(() => _search.SearchAsync(q, 10, Ct));
    }

    [Fact]
    public async Task REQ051_LimitIsRespected()
    {
        for (var i = 0; i < 30; i++)
        {
            await Add($@"C:\N{i}", $"Common {i}");
        }

        (await _search.SearchAsync("common", 7, Ct)).Count.ShouldBe(7);
        (await _search.SearchAsync("", 7, Ct)).Count.ShouldBe(7);
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => _search.SearchAsync("x", 0, Ct));
    }
}
