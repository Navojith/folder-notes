using System.Globalization;
using FolderNotes.App.Search;
using FolderNotes.App.Services;
using FolderNotes.Core.Domain;
using FolderNotes.Core.Identity;
using FolderNotes.Core.Search;
using FolderNotes.Core.Storage;
using Microsoft.Extensions.Time.Testing;
using Serilog.Core;

namespace FolderNotes.App.Tests.Search;

/// <summary>specs/05 § Search palette behaviour, REQ-050…053; real store and search, fake clock.</summary>
public sealed class SearchPaletteViewModelTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fn-palette-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero));
    private readonly List<string> _opened = [];
    private SqliteNoteStore _store = null!;
    private SearchService _search = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _store = await SqliteNoteStore.OpenAsync(new SqliteNoteStoreOptions
        {
            DatabasePath = Path.Combine(_dir, "notes.db"),
            Identity = new FolderIdentityService(),
            Clock = _clock,
        });
        _search = new SearchService(_store);
    }

    public async ValueTask DisposeAsync()
    {
        _search.Dispose();
        await _store.DisposeAsync();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task REQ050_EmptyBox_ListsTheMostRecentNotes()
    {
        for (var i = 0; i < 10; i++)
        {
            await AddAsync($@"D:\N{i}", $"Note {i}");
        }

        using var vm = NewVm();
        await vm.OpenAsync("");

        vm.Results.Count.ShouldBe(SearchPaletteViewModel.RecentCount);
        vm.Results[0].Row.Title.ShouldBe("Note 9");
        vm.SelectedIndex.ShouldBe(0);
        vm.CountText.ShouldBe("8 results");
        vm.FooterText.ShouldBe(SearchPaletteViewModel.Hints);
    }

    [Fact]
    public async Task REQ051_Typing_SearchesAfterTheDebounce_WithHighlights()
    {
        await AddAsync(@"D:\Clients\Acme Ltd", "Client: Acme Ltd");
        await AddAsync(@"D:\Other", "Other");
        using var vm = NewVm();
        await vm.OpenAsync("");

        vm.SetQuery("acm", 3);
        vm.Results.Count.ShouldBe(2); // not yet
        _clock.Advance(SearchPaletteViewModel.DebounceDelay);
        await vm.WhenIdleAsync();

        var hit = vm.Results.Single();
        hit.Row.Title.ShouldBe("Client: Acme Ltd");
        hit.TitleRanges.ShouldBe([new HighlightRange(8, 3)]);
        hit.PathText.ShouldBe(@"D:\Clients\Acme Ltd");
        hit.PathRanges.ShouldBe([new HighlightRange(11, 3)]);
        vm.CountText.ShouldBe("1 result");
    }

    [Fact]
    public async Task REQ051_OnlyTheLastKeystrokeSearches()
    {
        await AddAsync(@"D:\A", "alpha");
        await AddAsync(@"D:\B", "beta");
        using var vm = NewVm();
        await vm.OpenAsync("");

        vm.SetQuery("al", 2);
        _clock.Advance(TimeSpan.FromMilliseconds(60));
        vm.SetQuery("be", 2);
        _clock.Advance(TimeSpan.FromMilliseconds(60)); // the first timer was replaced
        vm.Results.Count.ShouldBe(2);
        _clock.Advance(TimeSpan.FromMilliseconds(60));
        await vm.WhenIdleAsync();

        vm.Results.Single().Row.Title.ShouldBe("beta");
    }

    [Fact]
    public async Task REQ052_ParseError_ShowsButTheRestRuns()
    {
        await AddAsync(@"D:\A", "alpha");
        using var vm = NewVm();
        await vm.OpenAsync("alpha status:bogus");

        vm.ErrorText.ShouldNotBeNull();
        vm.ErrorText.ShouldStartWith("Unknown status 'bogus'");
        vm.Results.Single().Row.Title.ShouldBe("alpha");
    }

    [Fact]
    public async Task REQ051_NoHits_SaysSo()
    {
        await AddAsync(@"D:\A", "alpha");
        using var vm = NewVm();
        await vm.OpenAsync("zzzzqqq");
        vm.Results.ShouldBeEmpty();
        vm.SelectedIndex.ShouldBe(-1);
        vm.EmptyText.ShouldBe("No notes match");
        vm.OpenFolder().ShouldBe(OpenFolderResult.NoSelection);
        vm.OpenNote().ShouldBeFalse();
        vm.CopyPath().ShouldBeNull();
    }

    [Fact]
    public async Task REQ053_Selection_Wraps()
    {
        await AddAsync(@"D:\A", "a");
        await AddAsync(@"D:\B", "b");
        await AddAsync(@"D:\C", "c");
        using var vm = NewVm();
        await vm.OpenAsync("");

        vm.MoveSelection(-1);
        vm.SelectedIndex.ShouldBe(2);
        vm.MoveSelection(1);
        vm.SelectedIndex.ShouldBe(0);
        vm.Select(1);
        vm.Selected!.Row.Title.ShouldBe("b");
    }

    [Fact]
    public async Task REQ053_Enter_OpensTheFolder_OrSaysItIsMissing()
    {
        await AddAsync(@"D:\Here", "here");
        await AddAsync(@"D:\Gone", "gone");
        using var vm = NewVm(path => path == @"D:\Here");
        await vm.OpenAsync("");

        vm.Selected!.Row.Title.ShouldBe("gone");
        vm.OpenFolder().ShouldBe(OpenFolderResult.FolderMissing);
        vm.FooterText.ShouldBe("Folder not found");
        _clock.Advance(SearchPaletteViewModel.MessageDuration);
        vm.FooterText.ShouldBe(SearchPaletteViewModel.Hints);

        vm.MoveSelection(1);
        vm.OpenFolder().ShouldBe(OpenFolderResult.Opened);
        _opened.ShouldBe([@"D:\Gone", @"D:\Here"]);
    }

    [Fact]
    public async Task REQ053_CtrlEnter_RequestsTheNote_CtrlC_GivesThePath()
    {
        var note = await AddAsync(@"D:\Clients\Acme", "acme");
        using var vm = NewVm();
        Guid? requested = null;
        vm.OpenNoteRequested += (_, id) => requested = id;
        await vm.OpenAsync("acme");

        vm.OpenNote().ShouldBeTrue();
        requested.ShouldBe(note.Id);

        vm.CopyPath().ShouldBe(@"D:\Clients\Acme");
        vm.FooterText.ShouldBe("Path copied");
    }

    [Fact]
    public async Task REQ052_Autocomplete_ShowsAndAccepts()
    {
        await AddAsync(@"D:\A", "alpha", n => n with { Labels = [Label.Create("client", "blue")] });
        using var vm = NewVm();
        await vm.OpenAsync("");

        vm.SetQuery("label:cl", 8);
        vm.ShowSuggestions.ShouldBeTrue();
        vm.Suggestions.Select(s => s.Display).ShouldBe(["client"]);
        vm.SuggestionIndex.ShouldBe(0);

        var chosen = vm.AcceptSuggestion().ShouldNotBeNull();
        chosen.NewText.ShouldBe("label:client ");
        vm.ShowSuggestions.ShouldBeFalse();
        vm.Query.ShouldBe("label:client ");
        _clock.Advance(SearchPaletteViewModel.DebounceDelay);
        await vm.WhenIdleAsync();
        vm.Results.Single().Row.Title.ShouldBe("alpha");
    }

    [Fact]
    public async Task REQ052_Autocomplete_EscClosesSuggestionsFirst_AndArrowsMoveThem()
    {
        using var vm = NewVm();
        await vm.OpenAsync("");
        vm.SetQuery("due:", 4);
        vm.MoveSelection(1);
        vm.SuggestionIndex.ShouldBe(1);
        vm.MoveSelection(-2);
        vm.SuggestionIndex.ShouldBe(3);

        vm.DismissSuggestions().ShouldBeTrue();
        vm.ShowSuggestions.ShouldBeFalse();
        vm.DismissSuggestions().ShouldBeFalse(); // then Esc closes the palette
    }

    [Fact]
    public async Task REQ050_Reopening_RunsTheNewQueryRightAway()
    {
        await AddAsync(@"D:\A", "alpha");
        await AddAsync(@"D:\B", "beta");
        using var vm = NewVm();
        await vm.OpenAsync("alpha");
        vm.Results.Single().Row.Title.ShouldBe("alpha");
        vm.CopyPath();

        await vm.OpenAsync("beta");
        vm.Results.Single().Row.Title.ShouldBe("beta");
        vm.FooterText.ShouldBe(SearchPaletteViewModel.Hints); // the message is cleared
    }

    [Fact]
    public async Task REQ051_FuzzyHits_AreNotHighlighted()
    {
        await AddAsync(@"D:\Invoices", "Invoices 2025");
        using var vm = NewVm();
        await vm.OpenAsync("invioces"); // transposition
        var hit = vm.Results.Single();
        hit.Kind.ShouldBe(SearchMatchKind.Fuzzy);
        hit.TitleRanges.ShouldBeEmpty();
    }

    private SearchPaletteViewModel NewVm(Func<string, bool>? exists = null) =>
        new(_search, _store, new InlineDispatcher(), _clock, Logger.None,
            path =>
            {
                _opened.Add(path);
                return exists?.Invoke(path) ?? true;
            },
            TimeZoneInfo.Utc, CultureInfo.InvariantCulture);

    private async Task<Note> AddAsync(string path, string title, Func<Note, Note>? change = null)
    {
        _clock.Advance(TimeSpan.FromSeconds(1));
        var note = Note.CreateFor(new FolderRef(path.ToUpperInvariant(), path, null, null), _clock) with { Title = title };
        return await _store.UpsertAsync(change is null ? note : change(note), Ct);
    }

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public bool Post(Action action)
        {
            action();
            return true;
        }
    }
}
