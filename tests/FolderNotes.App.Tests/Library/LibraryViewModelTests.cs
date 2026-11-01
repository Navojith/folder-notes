#pragma warning disable xUnit1069 // Timeouts guard against a hang in the reload/undo plumbing; the store calls already take Ct.
using System.Globalization;
using FolderNotes.App.Editor;
using FolderNotes.App.Library;
using FolderNotes.App.Services;
using FolderNotes.Core.Domain;
using FolderNotes.Core.Identity;
using FolderNotes.Core.Storage;
using Microsoft.Extensions.Time.Testing;
using Serilog.Core;

namespace FolderNotes.App.Tests.Library;

/// <summary>specs/05 § Library behaviour, 02 § Library, ADR-017; real SQLite store, fake clock and events.</summary>
public sealed class LibraryViewModelTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fn-library-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero));
    private readonly FolderIdentityService _identity = new();
    private readonly FakeEvents _events = new();
    private SqliteNoteStore _store = null!;
    private CountingNotes _notes = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _store = await SqliteNoteStore.OpenAsync(new SqliteNoteStoreOptions
        {
            DatabasePath = Path.Combine(_dir, "notes.db"),
            Identity = _identity,
            Clock = _clock,
        });
        _notes = new CountingNotes(_store);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ───────────── loading ─────────────

    [Fact(Timeout = 20_000)]
    public async Task REQ054_Load_ListsLiveNotes_NewestFirst_IncludingOrphaned()
    {
        var a = await AddAsync(@"D:\A", "Alpha");
        var b = await AddAsync(@"D:\B", "Beta");
        var gone = await AddAsync(@"D:\C", "Gone");
        await _store.DeleteAsync(gone.Id, Ct);
        await _store.SetOrphanedAsync(a.Id, true, null, Ct);

        using var vm = await OpenAsync();

        Titles(vm).ShouldBe(["Beta", "Alpha"]); // newest first; the orphan flag isn't an edit
        vm.CountText.ShouldBe("2 notes");
        vm.EmptyState.ShouldBe(LibraryEmptyState.None);
        var alpha = vm.Rows[1];
        alpha.IsOrphaned.ShouldBeTrue();
        alpha.FolderMissingText.ShouldBe("Folder not found");
        vm.Rows[0].Id.ShouldBe(b.Id);
    }

    [Fact(Timeout = 20_000)]
    public async Task REQ054_EmptyStates_NoNotes_And_NoMatches()
    {
        using var vm = await OpenAsync();
        vm.EmptyState.ShouldBe(LibraryEmptyState.NoNotes);
        vm.CountText.ShouldBe("0 notes");

        await AddAsync(@"D:\A", "Alpha");
        await vm.ReloadAsync();
        vm.CountText.ShouldBe("1 note");
        await vm.ToggleStatusFilterAsync(NoteStatus.Keep);
        vm.EmptyState.ShouldBe(LibraryEmptyState.NoMatches);
        await vm.ClearFiltersAsync();
        vm.EmptyState.ShouldBe(LibraryEmptyState.None);
    }

    // ───────────── sort ─────────────

    [Fact(Timeout = 20_000)]
    public async Task REQ054_EverySort()
    {
        await AddAsync(@"D:\Clients\Acme", "acme", n => n with { ReviewOn = new(2026, 12, 1) });
        await AddAsync(@"D:\ClientsOld", "Zed", n => n with { SafeAfter = new(2026, 11, 5) });
        await AddAsync(@"C:\Build", "build");
        using var vm = await OpenAsync();

        Titles(vm).ShouldBe(["build", "Zed", "acme"]);
        await vm.SetSortAsync(NoteSort.Title);
        Titles(vm).ShouldBe(["acme", "build", "Zed"]);
        await vm.SetSortAsync(NoteSort.DueDate);
        Titles(vm).ShouldBe(["Zed", "acme", "build"]); // no due date last
        await vm.SetSortAsync(NoteSort.Path);
        Titles(vm).ShouldBe(["build", "acme", "Zed"]); // hierarchical (O-9)
        vm.Sort.ShouldBe(NoteSort.Path);
    }

    // ───────────── filters ─────────────

    [Fact(Timeout = 20_000)]
    public async Task REQ054_LabelFilters_AreAllOf_StatusFilters_AreAnyOf()
    {
        await AddAsync(@"D:\A", "A", n => n with { Status = NoteStatus.Keep, Labels = [Label.Create("client", "blue"), Label.Create("personal", "green")] });
        await AddAsync(@"D:\B", "B", n => n with { Status = NoteStatus.Archive, Labels = [Label.Create("client", "blue")] });
        await AddAsync(@"D:\C", "C", n => n with { Status = NoteStatus.Review });
        using var vm = await OpenAsync();
        await vm.SetSortAsync(NoteSort.Title);

        await vm.ToggleLabelFilterAsync("CLIENT");
        Titles(vm).ShouldBe(["A", "B"]);
        await vm.ToggleLabelFilterAsync("personal");
        Titles(vm).ShouldBe(["A"]);
        vm.IsLabelFiltered("client").ShouldBeTrue();

        await vm.ClearFiltersAsync();
        await vm.ToggleStatusFilterAsync(NoteStatus.Archive);
        await vm.ToggleStatusFilterAsync(NoteStatus.Review);
        Titles(vm).ShouldBe(["B", "C"]);
        vm.ActiveFilters.Select(f => f.Text).ShouldBe(["Archive", "Review"]);
        vm.HasFilters.ShouldBeTrue();

        await vm.RemoveFilterAsync(vm.ActiveFilters[0]);
        Titles(vm).ShouldBe(["C"]);
        vm.ActiveFilters.Single().RemoveName.ShouldBe("Remove filter Review");

        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => vm.ToggleStatusFilterAsync(NoteStatus.None));
    }

    [Fact(Timeout = 20_000)]
    public async Task REQ054_FilterChips_ListLabelsThenStatuses()
    {
        using var vm = await OpenAsync();
        await vm.ToggleStatusFilterAsync(NoteStatus.Keep);
        await vm.ToggleLabelFilterAsync("client");
        vm.ActiveFilters.Select(f => f.Text).ShouldBe(["client", "Keep"]);
        await vm.ToggleLabelFilterAsync("client");
        vm.ActiveFilters.Select(f => f.Text).ShouldBe(["Keep"]);
    }

    [Fact(Timeout = 20_000)]
    public async Task REQ054_DeletedLabel_DropsOutOfTheFilters()
    {
        await AddAsync(@"D:\A", "A", n => n with { Labels = [Label.Create("temp", "red")] });
        using var vm = await OpenAsync();
        await vm.ToggleLabelFilterAsync("temp");
        Titles(vm).ShouldBe(["A"]);

        var temp = (await _store.ListLabelsAsync(Ct)).Single(l => l.Name == "temp");
        await _store.DeleteLabelAsync(temp.Id, Ct);
        _events.RaiseLabels();
        await vm.WhenIdleAsync();

        vm.HasFilters.ShouldBeFalse();
        Titles(vm).ShouldBe(["A"]);
        vm.AllLabels.ShouldNotContain(l => l.Name == "temp");
    }

    // ───────────── keeping in sync (ADR-017) ─────────────

    [Fact(Timeout = 20_000)]
    public async Task ADR017_Edit_UpdatesTheRowInPlace_WithoutMovingIt()
    {
        var older = await AddAsync(@"D:\Old", "Old");
        await AddAsync(@"D:\New", "New");
        using var vm = await OpenAsync();
        var row = vm.Rows[1];
        row.Id.ShouldBe(older.Id);

        _clock.Advance(TimeSpan.FromHours(2));
        await _store.UpsertAsync(older with { Title = "Old, edited", Status = NoteStatus.Archive }, Ct);
        var lists = _notes.Lists;
        await vm.ApplyChangeAsync(new NoteChangedEventArgs(older.Id, NoteChangeKind.Updated, older.Folder.PathNorm));

        vm.Rows[1].ShouldBeSameAs(row); // not moved to the top, not re-created
        row.Title.ShouldBe("Old, edited");
        row.Status.ShouldBe(NoteStatus.Archive);
        row.EditedText.ShouldBe("Edited just now");
        _notes.Lists.ShouldBe(lists); // no reload
    }

    [Fact(Timeout = 20_000)]
    public async Task ADR017_EditedRow_StaysEvenIfItNoLongerMatchesTheFilter()
    {
        var a = await AddAsync(@"D:\A", "A", n => n with { Status = NoteStatus.Keep });
        using var vm = await OpenAsync();
        await vm.ToggleStatusFilterAsync(NoteStatus.Keep);

        await _store.UpsertAsync(a with { Status = NoteStatus.Review }, Ct);
        await vm.ApplyChangeAsync(new NoteChangedEventArgs(a.Id, NoteChangeKind.Updated, a.Folder.PathNorm));
        vm.Rows.Single().Status.ShouldBe(NoteStatus.Review);

        await vm.ReloadAsync();
        vm.Rows.ShouldBeEmpty();
    }

    [Fact(Timeout = 20_000)]
    public async Task ADR017_Delete_RemovesTheRow_AndRestoreBringsItBack()
    {
        var a = await AddAsync(@"D:\A", "A");
        using var vm = await OpenAsync();

        await _store.DeleteAsync(a.Id, Ct);
        await vm.ApplyChangeAsync(new NoteChangedEventArgs(a.Id, NoteChangeKind.Deleted, a.Folder.PathNorm));
        vm.Rows.ShouldBeEmpty();
        vm.CountText.ShouldBe("0 notes");
        vm.EmptyState.ShouldBe(LibraryEmptyState.NoNotes);

        await _store.RestoreAsync(a.Id, Ct);
        await vm.ApplyChangeAsync(new NoteChangedEventArgs(a.Id, NoteChangeKind.Restored, a.Folder.PathNorm));
        Titles(vm).ShouldBe(["A"]);
        vm.EmptyState.ShouldBe(LibraryEmptyState.None);
    }

    [Theory(Timeout = 20_000)]
    [InlineData(NoteChangeKind.Created)]
    [InlineData(NoteChangeKind.Relinked)]
    [InlineData(NoteChangeKind.OrphanStateChanged)]
    public async Task ADR017_StructuralChanges_Reload(NoteChangeKind kind)
    {
        using var vm = await OpenAsync();
        var b = await AddAsync(@"D:\B", "B");
        var lists = _notes.Lists;

        await vm.ApplyChangeAsync(new NoteChangedEventArgs(b.Id, kind, b.Folder.PathNorm));

        _notes.Lists.ShouldBe(lists + 1);
        Titles(vm).ShouldBe(["B"]);
    }

    [Fact(Timeout = 20_000)]
    public async Task ADR017_LabelChange_Reloads()
    {
        var a = await AddAsync(@"D:\A", "A", n => n with { Labels = [Label.Create("client", "blue")] });
        using var vm = await OpenAsync();
        var client = (await _store.ListLabelsAsync(Ct)).Single(l => l.Name == "client");

        await _store.UpdateLabelAsync(client with { Name = "customer" }, Ct);
        _events.RaiseLabels();
        await vm.WhenIdleAsync();

        vm.Rows.Single().Chips.Single().Name.ShouldBe("customer");
        _ = a;
    }

    [Fact(Timeout = 20_000)]
    public async Task ADR017_Reloads_AreCoalesced()
    {
        await AddAsync(@"D:\A", "A");
        using var vm = await OpenAsync();
        var lists = _notes.Lists;

        _notes.HoldLists();
        var first = vm.ReloadAsync();
        var more = Enumerable.Range(0, 5).Select(_ => vm.ReloadAsync()).ToArray();
        _notes.ReleaseLists();
        await first;
        await Task.WhenAll(more);

        _notes.Lists.ShouldBe(lists + 2); // the running one plus exactly one more
    }

    [Fact(Timeout = 20_000)]
    public async Task ADR017_EventsBeforeTheFirstLoad_AreIgnored()
    {
        using var vm = NewVm();
        await vm.ApplyChangeAsync(new NoteChangedEventArgs(Guid.NewGuid(), NoteChangeKind.Created, "X"));
        _notes.Lists.ShouldBe(0);
    }

    // ───────────── selection + detail ─────────────

    [Fact(Timeout = 20_000)]
    public async Task REQ081_SavedState_IsRestored_DeletedLabelsDropped_AndChangesReported()
    {
        await AddAsync(@"D:\A", "A", n => n with { Labels = [Label.Create("client", "blue")], Status = NoteStatus.Keep });
        await AddAsync(@"D:\B", "B", n => n with { Labels = [Label.Create("client", "blue")], Status = NoteStatus.Review });
        await AddAsync(@"D:\C", "C");
        var reported = new List<LibraryState>();
        using var vm = NewVm();
        vm.Restore(new LibraryState(NoteSort.Title, LibraryViewMode.Cards, ["client", "gone-label"], [NoteStatus.Keep, NoteStatus.Review]));
        vm.StateChanged = reported.Add;

        await vm.ActivateAsync();

        vm.Sort.ShouldBe(NoteSort.Title);
        vm.ViewMode.ShouldBe(LibraryViewMode.Cards);
        Titles(vm).ShouldBe(["A", "B"]);
        vm.LabelFilters.ShouldBe(["client"]);
        reported.Single().Labels.ShouldBe(["client"]); // the dropped label is saved away

        await vm.SetSortAsync(NoteSort.Path);
        vm.ViewMode = LibraryViewMode.List;
        await vm.ClearFiltersAsync();
        reported.Count.ShouldBe(4);
        reported[^1].Matches(new LibraryState(NoteSort.Path, LibraryViewMode.List, [], [])).ShouldBeTrue();

        vm.ViewMode = LibraryViewMode.List; // no change, no report
        reported.Count.ShouldBe(4);
    }

    [Fact(Timeout = 20_000)]
    public async Task ADR020_Relink_TheDetailEditorShowsTheNewFolder()
    {
        var a = await AddAsync(@"D:\A", "A");
        using var vm = await OpenAsync();
        await vm.SelectAsync(a.Id);
        var target = new FolderRef(@"E:\NEW HOME\A", @"E:\New home\A", null, null);
        await _store.RelinkAsync(a.Id, target, a.Folder, Ct);

        await vm.ApplyChangeAsync(new NoteChangedEventArgs(a.Id, NoteChangeKind.Relinked, target.PathNorm));

        vm.Detail!.FolderPath.ShouldBe(@"E:\New home\A");
        vm.Detail.SaveState.ShouldBe(SaveState.None);
        vm.Rows.Single().PathDisplay.ShouldBe(@"E:\New home\A");
    }

    [Fact(Timeout = 20_000)]
    public async Task REQ054_Select_OpensTheDetailEditor_AndSwitchingFlushesIt()
    {
        var a = await AddAsync(@"D:\A", "A");
        var b = await AddAsync(@"D:\B", "B");
        using var vm = await OpenAsync();

        await vm.SelectAsync(a.Id);
        var detail = vm.Detail.ShouldNotBeNull();
        detail.State.ShouldBe(EditorState.Editing);
        detail.NoteId.ShouldBe(a.Id);
        detail.Title = "A, edited in the detail pane";

        await vm.SelectAsync(b.Id); // flushes the pending autosave first
        vm.SelectedId.ShouldBe(b.Id);
        vm.Detail!.NoteId.ShouldBe(b.Id);
        (await _store.GetByIdAsync(a.Id, Ct))!.Title.ShouldBe("A, edited in the detail pane");

        await vm.SelectAsync(null);
        vm.Detail.ShouldBeNull();
        vm.SelectedId.ShouldBeNull();
    }

    [Fact(Timeout = 20_000)]
    public async Task REQ054_Selection_IsKeptAcrossReloads()
    {
        var a = await AddAsync(@"D:\A", "A");
        using var vm = await OpenAsync();
        await vm.SelectAsync(a.Id);
        var detail = vm.Detail;

        await vm.SetSortAsync(NoteSort.Title);
        await vm.ToggleStatusFilterAsync(NoteStatus.Keep); // hides it

        vm.SelectedId.ShouldBe(a.Id);
        vm.Detail.ShouldBeSameAs(detail);
    }

    [Fact(Timeout = 20_000)]
    public async Task REQ054_DeleteInTheDetail_ThenUndoTimeout_ClearsTheSelection()
    {
        var a = await AddAsync(@"D:\A", "A");
        using var vm = await OpenAsync();
        await vm.SelectAsync(a.Id);

        await vm.Detail!.DeleteAsync();
        await vm.ApplyChangeAsync(new NoteChangedEventArgs(a.Id, NoteChangeKind.Deleted, a.Folder.PathNorm));
        vm.Rows.ShouldBeEmpty();
        vm.Detail.State.ShouldBe(EditorState.Deleted); // the Undo bar stays

        _clock.Advance(NoteEditorViewModel.UndoWindow);
        vm.Detail.ShouldBeNull();
        vm.SelectedId.ShouldBeNull();
    }

    [Fact(Timeout = 20_000)]
    public async Task REQ054_OpenNote_ClearsFiltersThatHideIt()
    {
        var a = await AddAsync(@"D:\A", "A");
        using var vm = await OpenAsync();
        await vm.ToggleStatusFilterAsync(NoteStatus.Keep);
        vm.Rows.ShouldBeEmpty();

        await vm.OpenNoteAsync(a.Id);

        vm.HasFilters.ShouldBeFalse();
        Titles(vm).ShouldBe(["A"]);
        vm.SelectedId.ShouldBe(a.Id);
        vm.Detail!.State.ShouldBe(EditorState.Editing);
    }

    [Fact(Timeout = 20_000)]
    public async Task REQ054_OpenNote_UnknownId_ShowsMissing_AndKeepsFilters()
    {
        using var vm = await OpenAsync();
        await vm.ToggleStatusFilterAsync(NoteStatus.Keep);

        await vm.OpenNoteAsync(Guid.NewGuid());

        vm.HasFilters.ShouldBeTrue();
        vm.Detail!.State.ShouldBe(EditorState.Missing);
        vm.Detail.MissingMessage.ShouldBe("This note no longer exists.");
    }

    [Fact(Timeout = 20_000)]
    public async Task REQ054_OpenNote_BeforeTheFirstLoad_LoadsThenSelects()
    {
        var a = await AddAsync(@"D:\A", "A");
        using var vm = NewVm();
        await vm.OpenNoteAsync(a.Id);
        Titles(vm).ShouldBe(["A"]);
        vm.SelectedId.ShouldBe(a.Id);
    }

    [Fact(Timeout = 20_000)]
    public async Task REQ054_Activate_RefreshesRelativeTimes()
    {
        await AddAsync(@"D:\A", "A");
        using var vm = await OpenAsync();
        vm.Rows[0].EditedText.ShouldBe("Edited just now");

        _clock.Advance(TimeSpan.FromMinutes(5));
        await vm.ActivateAsync();
        vm.Rows[0].EditedText.ShouldBe("Edited 5 min ago");
        _notes.Lists.ShouldBe(1); // no reload, just the times
    }

    [Fact(Timeout = 20_000)]
    public async Task REQ012_ShowOnlyLabel_ReplacesEveryFilter()
    {
        await AddAsync(@"D:\A", "A", n => n with { Status = NoteStatus.Keep, Labels = [Label.Create("client", "blue")] });
        await AddAsync(@"D:\B", "B", n => n with { Labels = [Label.Create("client", "blue")] });
        using var vm = await OpenAsync();
        await vm.ToggleStatusFilterAsync(NoteStatus.Keep);
        await vm.ToggleLabelFilterAsync("personal");

        await vm.ShowOnlyLabelAsync("client");

        vm.ActiveFilters.Select(f => f.Text).ShouldBe(["client"]);
        Titles(vm).ShouldBe(["B", "A"]);
    }

    [Fact(Timeout = 20_000)]
    public async Task ADR018_LabelChange_RefreshesTheDetailEditor()
    {
        var a = await AddAsync(@"D:\A", "A", n => n with { Labels = [Label.Create("temp", "red")] });
        using var vm = await OpenAsync();
        await vm.SelectAsync(a.Id);
        var temp = vm.Detail!.Labels.Single();

        await _store.DeleteLabelAsync(temp.Id, Ct);
        _events.RaiseLabels();
        await vm.WhenIdleAsync();

        vm.Detail.Labels.ShouldBeEmpty();
        vm.Rows.Single().Chips.ShouldBeEmpty();
    }

    // ───────────── rows ─────────────

    [Fact(Timeout = 20_000)]
    public async Task REQ054_Row_ShowsThreeChipsPlusMore_AndAnAccessibleName()
    {
        await AddAsync(@"D:\Clients\Acme", "Client: Acme", n => n with
        {
            Status = NoteStatus.Keep,
            AccentColor = "teal",
            KeepUntil = new(2027, 1, 1),
            Labels = [Label.Create("a", "red"), Label.Create("b", "red"), Label.Create("c", "red"), Label.Create("d", "red"), Label.Create("e", "red")],
        });
        using var vm = await OpenAsync();
        var row = vm.Rows.Single();

        row.Chips.Select(l => l.Name).ShouldBe(["a", "b", "c"]);
        row.MoreLabels.ShouldBe("+2");
        row.HasAccent.ShouldBeTrue();
        row.AccentColor.ShouldBe("teal");
        row.NextDate.ShouldBe("Keep until 1 Jan 2027");
        row.AccessibleName.ShouldBe(@"Client: Acme, D:\Clients\Acme, Keep, labels a, b, c, d, e, Keep until 1 Jan 2027, edited just now");
    }

    // ───────────── helpers ─────────────

    private LibraryViewModel NewVm() =>
        new(_notes, _store, _events, () => new NoteEditorViewModel(_store, _store, _identity, _clock, new InlineDispatcher(), Logger.None, _ => true),
            _clock, Logger.None, TimeZoneInfo.Utc, CultureInfo.InvariantCulture);

    private async Task<LibraryViewModel> OpenAsync()
    {
        var vm = NewVm();
        await vm.ActivateAsync();
        return vm;
    }

    private async Task<Note> AddAsync(string path, string title, Func<Note, Note>? change = null)
    {
        _clock.Advance(TimeSpan.FromSeconds(1));
        var note = Note.CreateFor(new FolderRef(path.ToUpperInvariant(), path, null, null), _clock) with { Title = title };
        return await _store.UpsertAsync(change is null ? note : change(note), Ct);
    }

    private static string[] Titles(LibraryViewModel vm) => [.. vm.Rows.Select(r => r.Title)];

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public bool Post(Action action)
        {
            action();
            return true;
        }
    }

    private sealed class FakeEvents : INoteEvents
    {
        public event EventHandler<NoteChangedEventArgs>? NoteChanged;

        public event EventHandler? LabelsChanged;

        public void RaiseLabels() => LabelsChanged?.Invoke(this, EventArgs.Empty);

        public void Raise(NoteChangedEventArgs e) => NoteChanged?.Invoke(this, e);
    }

    /// <summary>Counts <c>ListAsync</c> calls and can hold them open.</summary>
    private sealed class CountingNotes(INoteRepository inner) : INoteRepository
    {
        private TaskCompletionSource? _gate;

        public int Lists { get; private set; }

        public event EventHandler<NoteChangedEventArgs>? NoteChanged
        {
            add => inner.NoteChanged += value;
            remove => inner.NoteChanged -= value;
        }

        private TaskCompletionSource? _held; // kept after the gate is consumed, so Release still works

        public void HoldLists() => _gate = _held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseLists() => _held?.TrySetResult();

        public async Task<IReadOnlyList<Note>> ListAsync(NoteQuery query, CancellationToken ct = default)
        {
            Lists++;
            if (_gate is { } gate)
            {
                _gate = null;
                await gate.Task;
            }

            return await inner.ListAsync(query, ct);
        }

        public Task<Note?> GetByFolderAsync(FolderRef folder, CancellationToken ct = default) => inner.GetByFolderAsync(folder, ct);

        public Task<Note?> GetByIdAsync(Guid id, CancellationToken ct = default) => inner.GetByIdAsync(id, ct);

        public Task<Note> UpsertAsync(Note note, CancellationToken ct = default) => inner.UpsertAsync(note, ct);

        public Task DeleteAsync(Guid id, CancellationToken ct = default) => inner.DeleteAsync(id, ct);

        public Task RestoreAsync(Guid id, CancellationToken ct = default) => inner.RestoreAsync(id, ct);

        public Task<int> PurgeDeletedAsync(TimeSpan olderThan, CancellationToken ct = default) => inner.PurgeDeletedAsync(olderThan, ct);

        public Task<Note> RelinkAsync(Guid id, FolderRef newFolder, FolderRef? expectedFolder, CancellationToken ct = default) => inner.RelinkAsync(id, newFolder, expectedFolder, ct);

        public Task<bool> SetOrphanedAsync(Guid id, bool orphaned, FolderRef? expectedFolder, CancellationToken ct = default) => inner.SetOrphanedAsync(id, orphaned, expectedFolder, ct);
    }
}
