using FolderNotes.App.Editor;
using FolderNotes.App.Services;
using FolderNotes.Core.Domain;
using FolderNotes.Core.Identity;
using FolderNotes.Core.Storage;
using Microsoft.Extensions.Time.Testing;
using Serilog.Core;

namespace FolderNotes.App.Tests.Editor;

/// <summary>specs/05 § Editor behaviour and ADR-014, against the real SQLite store and a fake clock.</summary>
public sealed class NoteEditorViewModelTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fn-editor-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 31, 9, 0, 0, TimeSpan.Zero));
    private readonly FolderIdentityService _identity = new();
    private SqliteNoteStore _store = null!;
    private string _folder = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _folder = Path.Combine(_dir, "Acme Ltd");
        Directory.CreateDirectory(_folder);
        _store = await SqliteNoteStore.OpenAsync(new SqliteNoteStoreOptions
        {
            DatabasePath = Path.Combine(_dir, "notes.db"),
            Identity = _identity,
        });
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

    // ───────────── opening (REQ-001, 003, 004) ─────────────

    [Fact]
    public async Task REQ003_NewFolder_OpensADraftTitledAfterTheFolder_AndStoresNothing()
    {
        using var vm = await OpenFolderAsync();

        vm.State.ShouldBe(EditorState.Editing);
        vm.IsDraft.ShouldBeTrue();
        vm.CanDelete.ShouldBeFalse();
        vm.Title.ShouldBe("Acme Ltd");
        vm.SaveText.ShouldBe("");
        vm.FolderExists.ShouldBeTrue();
        (await StoredAsync()).ShouldBeNull();
    }

    [Fact]
    public async Task REQ003_UntouchedDraft_ClosedWithoutSaving_CreatesNothing()
    {
        using var vm = await OpenFolderAsync();
        await vm.FlushAsync();
        (await StoredAsync()).ShouldBeNull();
    }

    [Fact]
    public async Task REQ003_CtrlEnter_SavesAnUntouchedDraft()
    {
        using var vm = await OpenFolderAsync();
        await vm.SaveNowAsync();

        var stored = (await StoredAsync()).ShouldNotBeNull();
        stored.Title.ShouldBe("Acme Ltd");
        stored.Id.ShouldBe(vm.NoteId!.Value);
        vm.IsDraft.ShouldBeFalse();
        vm.SaveState.ShouldBe(SaveState.Saved);
    }

    [Fact]
    public async Task REQ001_REQ004_FolderWithANote_OpensThatNote()
    {
        var existing = await _store.UpsertAsync(Note.CreateFor(_identity.Resolve(_folder)) with { Title = "Client: Acme", BodyMd = "Ask Sam." }, Ct);

        using var vm = await OpenFolderAsync();

        vm.IsDraft.ShouldBeFalse();
        vm.NoteId.ShouldBe(existing.Id);
        vm.Title.ShouldBe("Client: Acme");
        vm.Body.ShouldBe("Ask Sam.");
        vm.CanDelete.ShouldBeTrue();
    }

    [Fact]
    public async Task MissingFolder_ShowsMissingState()
    {
        using var vm = NewVm();
        await vm.LoadForFolderAsync(Path.Combine(_dir, "nope"), Ct);
        vm.State.ShouldBe(EditorState.Missing);
        vm.MissingMessage.ShouldBe("This folder doesn't exist.");
    }

    [Fact]
    public async Task OpenById_UnknownOrDeleted_ShowsMissingState()
    {
        using var unknown = NewVm();
        await unknown.LoadByIdAsync(Guid.NewGuid(), Ct);
        unknown.State.ShouldBe(EditorState.Missing);

        var note = await _store.UpsertAsync(Note.CreateFor(_identity.Resolve(_folder)), Ct);
        await _store.DeleteAsync(note.Id, Ct);
        using var deleted = NewVm();
        await deleted.LoadByIdAsync(note.Id, Ct);
        deleted.State.ShouldBe(EditorState.Missing);
        deleted.MissingMessage.ShouldBe("This note no longer exists.");
    }

    [Fact]
    public async Task OpenById_LoadsTheNote_AndReportsAMissingFolder()
    {
        var gone = Path.Combine(_dir, "Gone");
        Directory.CreateDirectory(gone);
        var note = await _store.UpsertAsync(Note.CreateFor(_identity.Resolve(gone)) with { Title = "Old" }, Ct);
        Directory.Delete(gone);

        using var vm = NewVm();
        await vm.LoadByIdAsync(note.Id, Ct);
        vm.State.ShouldBe(EditorState.Editing);
        vm.Title.ShouldBe("Old");
        vm.FolderExists.ShouldBeFalse();
    }

    // ───────────── autosave (REQ-005) ─────────────

    [Fact]
    public async Task REQ005_Autosave_Runs500msAfterTheLastChange()
    {
        using var vm = await OpenFolderAsync();

        vm.Title = "Client";
        vm.SaveState.ShouldBe(SaveState.Pending);
        vm.SaveText.ShouldBe("Saving…");
        _clock.Advance(TimeSpan.FromMilliseconds(499));
        await vm.WhenIdleAsync();
        (await StoredAsync()).ShouldBeNull();

        vm.Title = "Client: Acme"; // restarts the debounce
        _clock.Advance(TimeSpan.FromMilliseconds(499));
        await vm.WhenIdleAsync();
        (await StoredAsync()).ShouldBeNull();

        _clock.Advance(TimeSpan.FromMilliseconds(1));
        await vm.WhenIdleAsync();
        (await StoredAsync()).ShouldNotBeNull().Title.ShouldBe("Client: Acme");
        vm.SaveState.ShouldBe(SaveState.Saved);
        vm.SaveText.ShouldBe("Saved");
        vm.IsDraft.ShouldBeFalse();
    }

    [Fact]
    public async Task REQ005_REQ020_EveryFieldIsSaved()
    {
        using var vm = await OpenFolderAsync();
        vm.Title = "Acme";
        vm.Body = "- [ ] invoices";
        vm.Status = NoteStatus.Keep;
        vm.AccentColor = "teal";
        vm.KeepUntil = new DateOnly(2027, 1, 1);
        vm.ReviewOn = new DateOnly(2026, 12, 1);
        vm.SafeAfter = new DateOnly(2028, 6, 30);
        vm.AddLabel("client").ShouldBeTrue();
        await vm.FlushAsync();

        var stored = (await StoredAsync()).ShouldNotBeNull();
        stored.BodyMd.ShouldBe("- [ ] invoices");
        stored.Status.ShouldBe(NoteStatus.Keep);
        stored.AccentColor.ShouldBe("teal");
        stored.KeepUntil.ShouldBe(new DateOnly(2027, 1, 1));
        stored.ReviewOn.ShouldBe(new DateOnly(2026, 12, 1));
        stored.SafeAfter.ShouldBe(new DateOnly(2028, 6, 30));
        stored.Labels.Select(l => l.Name).ShouldBe(["client"]);
    }

    [Fact]
    public async Task REQ005_AChangeDuringASave_IsSavedAfterIt()
    {
        var gated = new GatedNotes(_store);
        using var vm = await OpenFolderAsync(gated);
        vm.Title = "First";
        gated.Hold();
        _clock.Advance(NoteEditorViewModel.AutosaveDelay);
        vm.SaveState.ShouldBe(SaveState.Saving);

        vm.Title = "Second";
        _clock.Advance(NoteEditorViewModel.AutosaveDelay); // timer fires while the first save runs
        gated.Release();
        await vm.WhenIdleAsync();

        (await StoredAsync()).ShouldNotBeNull().Title.ShouldBe("Second");
        gated.Upserts.ShouldBe(2);
        vm.SaveState.ShouldBe(SaveState.Saved);
    }

    [Fact]
    public async Task REQ002_InvalidTitle_ShowsAnInlineError_AndDoesNotSave()
    {
        using var vm = await OpenFolderAsync();
        vm.Title = "   ";
        vm.TitleError.ShouldBe("A title is required.");
        vm.SaveState.ShouldBe(SaveState.Invalid);
        vm.SaveText.ShouldBe("Not saved: A title is required.");

        _clock.Advance(TimeSpan.FromSeconds(5));
        await vm.WhenIdleAsync();
        (await StoredAsync()).ShouldBeNull();

        vm.Title = new string('x', 121);
        vm.TitleError.ShouldBe("The title can be at most 120 characters.");
        vm.Title = "Fine";
        vm.TitleError.ShouldBeNull();
        vm.SaveState.ShouldBe(SaveState.Pending);
    }

    [Fact]
    public async Task REQ005_Flush_PutsAnInvalidTitleBack_AndSavesTheRest()
    {
        var existing = await _store.UpsertAsync(Note.CreateFor(_identity.Resolve(_folder)) with { Title = "Kept title" }, Ct);
        using var vm = await OpenFolderAsync();
        vm.Body = "new body";
        vm.Title = "";
        await vm.FlushAsync();

        vm.Title.ShouldBe("Kept title");
        var stored = (await _store.GetByIdAsync(existing.Id, Ct)).ShouldNotBeNull();
        stored.Title.ShouldBe("Kept title");
        stored.BodyMd.ShouldBe("new body");
    }

    [Fact]
    public async Task REQ005_Flush_OnADraftWithAnEmptyTitle_UsesTheSuggestedTitle()
    {
        using var vm = await OpenFolderAsync();
        vm.Body = "notes";
        vm.Title = "";
        await vm.FlushAsync();
        (await StoredAsync()).ShouldNotBeNull().Title.ShouldBe("Acme Ltd");
    }

    [Fact]
    public async Task REQ005_StoreFailure_ShowsCouldNotSave_AndTheNextChangeRetries()
    {
        var gated = new GatedNotes(_store) { FailNext = true };
        using var vm = await OpenFolderAsync(gated);
        vm.Title = "One";
        _clock.Advance(NoteEditorViewModel.AutosaveDelay);
        await vm.WhenIdleAsync();
        vm.SaveState.ShouldBe(SaveState.Failed);
        vm.SaveText.ShouldBe("Couldn't save: disk full");

        vm.Title = "Two";
        _clock.Advance(NoteEditorViewModel.AutosaveDelay);
        await vm.WhenIdleAsync();
        vm.SaveState.ShouldBe(SaveState.Saved);
        (await StoredAsync()).ShouldNotBeNull().Title.ShouldBe("Two");
    }

    // ───────────── delete / undo (REQ-006) ─────────────

    [Fact]
    public async Task REQ006_DeleteThenUndo_RestoresTheSameIdAndContent()
    {
        using var vm = await OpenFolderAsync();
        vm.Title = "Acme";
        vm.Body = "body";
        vm.AddLabel("client");
        await vm.FlushAsync();
        var before = (await StoredAsync()).ShouldNotBeNull();

        await vm.DeleteAsync();
        vm.State.ShouldBe(EditorState.Deleted);
        vm.CanDelete.ShouldBeFalse();
        (await StoredAsync()).ShouldBeNull();

        _clock.Advance(TimeSpan.FromSeconds(9));
        await vm.UndoDeleteAsync();

        vm.State.ShouldBe(EditorState.Editing);
        var after = (await StoredAsync()).ShouldNotBeNull();
        after.Id.ShouldBe(before.Id);
        after.Title.ShouldBe(before.Title);
        after.BodyMd.ShouldBe(before.BodyMd);
        after.Labels.Select(l => l.Id).ShouldBe(before.Labels.Select(l => l.Id));
        vm.Title.ShouldBe("Acme");
    }

    [Fact]
    public async Task REQ006_UndoExpiresAfter10Seconds()
    {
        using var vm = await OpenFolderAsync();
        await vm.SaveNowAsync();
        await vm.DeleteAsync();
        var expired = 0;
        vm.UndoExpired += (_, _) => expired++;

        _clock.Advance(TimeSpan.FromMilliseconds(9_999));
        expired.ShouldBe(0);
        _clock.Advance(TimeSpan.FromMilliseconds(1));
        expired.ShouldBe(1);

        await vm.UndoDeleteAsync(); // still allowed while the window is open; the window decides to close
        vm.State.ShouldBe(EditorState.Editing);
    }

    [Fact]
    public async Task REQ006_Delete_DropsAPendingSave_AndIsDisabledForDrafts()
    {
        using var vm = await OpenFolderAsync();
        await vm.DeleteAsync(); // draft: no-op
        vm.State.ShouldBe(EditorState.Editing);

        await vm.SaveNowAsync();
        vm.Title = "typed just before delete";
        await vm.DeleteAsync();
        _clock.Advance(TimeSpan.FromSeconds(1));
        await vm.UndoDeleteAsync();

        (await StoredAsync()).ShouldNotBeNull().Title.ShouldBe("Acme Ltd");
    }

    // ───────────── labels (REQ-011) ─────────────

    [Fact]
    public async Task REQ011_Suggestions_PrefixFirst_ThenSubstring_ExcludingLabelsOnTheNote()
    {
        // "client" (blue) and "personal" are starter labels (REQ-013).
        foreach (var (name, color) in new[] { ("old-client", "red"), ("clinic", "green") })
        {
            await _store.CreateLabelAsync(name, color, Ct);
        }

        using var vm = await OpenFolderAsync();
        vm.SuggestLabels("cli").Select(s => s.Text).ShouldBe(["client", "clinic", "old-client", "Create label \"cli\""]);

        vm.AddLabel("CLIENT").ShouldBeTrue(); // matches the existing label, ignoring case
        vm.Labels.Single().Color.ShouldBe("blue");
        vm.SuggestLabels("cli").Select(s => s.Text).ShouldBe(["clinic", "old-client", "Create label \"cli\""]);
        vm.SuggestLabels("client").Select(s => s.Text).ShouldBe(["old-client"]); // exact match exists: no Create
        vm.SuggestLabels("").Count.ShouldBe(6); // 5 starters + 2 created − client
    }

    [Fact]
    public async Task REQ011_Suggestions_AreCappedAtEight()
    {
        for (var i = 0; i < 12; i++)
        {
            await _store.CreateLabelAsync($"tag{i:00}", "blue", Ct);
        }

        using var vm = await OpenFolderAsync();
        var list = vm.SuggestLabels("tag");
        list.Count.ShouldBe(NoteEditorViewModel.MaxSuggestions + 1);
        list[^1].CreateName.ShouldBe("tag");
    }

    [Fact]
    public async Task REQ011_InlineCreatedLabel_GetsTheLeastUsedColour_AndIsCreatedOnSave()
    {
        // Starters use blue, amber, indigo, green, red; add orange so the first unused is yellow.
        await _store.CreateLabelAsync("b", "orange", Ct);
        using var vm = await OpenFolderAsync();

        vm.NextLabelColor().ShouldBe("yellow");
        vm.AddLabel(vm.SuggestLabels("brand-new").Single()).ShouldBeTrue();
        vm.Labels.Single().Color.ShouldBe("yellow");
        vm.NextLabelColor().ShouldBe("lime");
        await vm.FlushAsync();

        (await _store.ListLabelsAsync(Ct)).Select(l => (l.Name, l.Color)).ShouldContain(("brand-new", "yellow"));
        vm.SuggestLabels("brand-new").ShouldBeEmpty(); // now known and already on the note: no suggestion, no Create
    }

    [Theory]
    [InlineData("", "A label name is required.")]
    [InlineData("has\"quote", "'has\"quote' isn't a valid label name.")]
    [InlineData("0123456789012345678901234567890123456789x", "A label name can be at most 40 characters.")]
    public async Task REQ011_InvalidLabel_IsRejectedWithAnInlineError(string name, string error)
    {
        using var vm = await OpenFolderAsync();
        vm.AddLabel(name).ShouldBeFalse();
        vm.LabelError.ShouldBe(error);
        vm.Labels.ShouldBeEmpty();
        vm.SaveState.ShouldBe(SaveState.None);
    }

    [Fact]
    public async Task REQ011_DuplicateLabel_IsRejected_AndRemoveWorks()
    {
        using var vm = await OpenFolderAsync();
        vm.AddLabel("client").ShouldBeTrue();
        vm.AddLabel("Client").ShouldBeFalse();
        vm.LabelError.ShouldBe("The label 'client' is already on this note."); // resolved to the existing label

        vm.RemoveLabel(vm.Labels[0]);
        vm.Labels.ShouldBeEmpty();
        vm.LabelError.ShouldBeNull();
        vm.SaveState.ShouldBe(SaveState.Pending);
    }

    // ───────────── helpers ─────────────

    private NoteEditorViewModel NewVm(INoteRepository? notes = null) =>
        new(notes ?? _store, _store, _identity, _clock, new InlineDispatcher(), Logger.None);

    private async Task<NoteEditorViewModel> OpenFolderAsync(INoteRepository? notes = null)
    {
        var vm = NewVm(notes);
        await vm.LoadForFolderAsync(_folder, Ct);
        return vm;
    }

    private Task<Note?> StoredAsync() => _store.GetByFolderAsync(_identity.Resolve(_folder), Ct);

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public bool Post(Action action)
        {
            action();
            return true;
        }
    }

    /// <summary>Delegates to the store; can hold an upsert open or fail the next one.</summary>
    private sealed class GatedNotes(INoteRepository inner) : INoteRepository
    {
        private TaskCompletionSource? _gate;
        private TaskCompletionSource? _held;

        public int Upserts { get; private set; }

        public bool FailNext { get; set; }

        public event EventHandler<NoteChangedEventArgs>? NoteChanged
        {
            add => inner.NoteChanged += value;
            remove => inner.NoteChanged -= value;
        }

        public void Hold() => _gate = _held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _held?.TrySetResult();

        public async Task<Note> UpsertAsync(Note note, CancellationToken ct = default)
        {
            Upserts++;
            if (FailNext)
            {
                FailNext = false;
                throw new IOException("disk full");
            }

            if (_gate is { } gate)
            {
                _gate = null;
                await gate.Task;
            }

            return await inner.UpsertAsync(note, ct);
        }

        public Task<Note?> GetByFolderAsync(FolderRef folder, CancellationToken ct = default) => inner.GetByFolderAsync(folder, ct);

        public Task<Note?> GetByIdAsync(Guid id, CancellationToken ct = default) => inner.GetByIdAsync(id, ct);

        public Task DeleteAsync(Guid id, CancellationToken ct = default) => inner.DeleteAsync(id, ct);

        public Task RestoreAsync(Guid id, CancellationToken ct = default) => inner.RestoreAsync(id, ct);

        public Task<IReadOnlyList<Note>> ListAsync(NoteQuery query, CancellationToken ct = default) => inner.ListAsync(query, ct);

        public Task<int> PurgeDeletedAsync(TimeSpan olderThan, CancellationToken ct = default) => inner.PurgeDeletedAsync(olderThan, ct);

        public Task<Note> RelinkAsync(Guid id, FolderRef newFolder, CancellationToken ct = default) => inner.RelinkAsync(id, newFolder, ct);

        public Task SetOrphanedAsync(Guid id, bool orphaned, CancellationToken ct = default) => inner.SetOrphanedAsync(id, orphaned, ct);
    }
}
