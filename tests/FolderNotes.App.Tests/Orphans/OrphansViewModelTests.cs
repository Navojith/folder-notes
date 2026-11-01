#pragma warning disable xUnit1069 // Timeouts guard against a hang in the reload/undo plumbing; the store calls already take Ct.
using System.Globalization;
using FolderNotes.App.Orphans;
using FolderNotes.App.Services;
using FolderNotes.Core.Domain;
using FolderNotes.Core.Identity;
using FolderNotes.Core.Storage;
using Microsoft.Extensions.Time.Testing;
using Serilog.Core;

namespace FolderNotes.App.Tests.Orphans;

/// <summary>specs/05 § Orphaned page, 03 § Relink rules, ADR-020; real store, real temp folders, fake picker and clock.</summary>
public sealed class OrphansViewModelTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fn-orphans-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero));
    private readonly FolderIdentityService _identity = new();
    private readonly FakeEvents _events = new();
    private readonly FakePicker _picker = new();
    private readonly List<(string Title, string Message)> _confirms = [];
    private bool _confirmAnswer = true;
    private SqliteNoteStore _store = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Root => Path.Combine(_dir, "folders");

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(Root);
        _store = await SqliteNoteStore.OpenAsync(new SqliteNoteStoreOptions
        {
            DatabasePath = Path.Combine(_dir, "notes.db"),
            Identity = _identity,
            Clock = _clock,
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

    // ───────────── list and reasons ─────────────

    [Fact(Timeout = 20_000)]
    public async Task REQ061_List_ShowsOrphansByPath_WithReasons_AndTheCount()
    {
        var gone = await AddAsync("Beta", "Beta");
        await AddAsync("Alpha", "Kept");
        Directory.Delete(Folder("Beta"));
        var unplugged = await _store.UpsertAsync(Note.CreateFor(
            new FolderRef(@"Q:\PHOTOS", @"Q:\Photos", "0123456789ABCDEF", "000000000000000000000000000000A1"), _clock) with { Title = "Photos" }, Ct);
        await new OrphanScanner(_store, _identity).ScanAsync(Ct);

        using var vm = Create();
        await vm.ActivateAsync();

        vm.Items.Select(i => i.Title).ShouldBe(["Beta", "Photos"]); // by path: C:\…\Beta before Q:\Photos
        vm.Count.ShouldBe(2);
        vm.CountText.ShouldBe("2 notes");
        vm.NavName.ShouldBe("Orphaned, 2 notes");
        vm.IsEmpty.ShouldBeFalse();
        var beta = vm.Items[0];
        beta.Id.ShouldBe(gone.Id);
        beta.Reason.ShouldBe(OrphanReason.NotFound);
        beta.ReasonText.ShouldBe("The folder was deleted, or moved to another drive.");
        beta.OrphanedText.ShouldBe("Orphaned 1 Nov 2026");
        beta.AccessibleName.ShouldBe($"Beta, {gone.Folder.PathDisplay}, The folder was deleted, or moved to another drive.");
        vm.Items[1].Id.ShouldBe(unplugged.Id);
        vm.Items[1].Reason.ShouldBe(OrphanReason.DriveUnavailable);
    }

    [Fact(Timeout = 20_000)]
    public async Task REQ061_NoOrphans_ShowsTheEmptyState_AndNoBadge()
    {
        await AddAsync("Alpha", "Alpha");
        using var vm = Create();
        await vm.ActivateAsync();

        vm.IsEmpty.ShouldBeTrue();
        vm.Count.ShouldBe(0);
        vm.NavName.ShouldBe("Orphaned");
    }

    [Fact(Timeout = 20_000)]
    public async Task REQ061_Activate_ChecksTheListedNotesAgain_AFolderThatIsBackLeavesTheList()
    {
        var back = await AddAsync("Back", "Back");
        await _store.SetOrphanedAsync(back.Id, true, null, Ct); // e.g. the drive was off during the last scan

        using var vm = Create();
        await vm.ActivateAsync();

        vm.Items.ShouldBeEmpty();
        (await _store.GetByIdAsync(back.Id, Ct))!.IsOrphaned.ShouldBeFalse();
    }

    [Fact(Timeout = 20_000)]
    public async Task REQ061_Conflict_ShowsWhereTheFolderIs()
    {
        // A's folder moved onto B's path (B's own folder was deleted first).
        var a = await AddAsync("A", "Alpha");
        var b = await AddAsync("B", "Beta");
        Directory.Delete(Folder("B"));
        Directory.Move(Folder("A"), Folder("B"));
        await new OrphanScanner(_store, _identity).ScanAsync(Ct);

        using var vm = Create();
        await vm.ActivateAsync();

        var alpha = vm.Items.Single(i => i.Id == a.Id);
        alpha.Reason.ShouldBe(OrphanReason.Conflict);
        alpha.ReasonText.ShouldBe($"The folder is now at {_identity.Resolve(Folder("B")).PathDisplay}, but another note is attached there.");
        vm.Items.Select(i => i.Id).ShouldContain(b.Id);
    }

    [Fact(Timeout = 20_000)]
    public async Task REQ061_CheckAgain_RunsTheFullScan_AndSaysWhatChanged()
    {
        await AddAsync("Alpha", "Alpha");
        await AddAsync("Beta", "Beta");
        using var vm = Create();
        await vm.ActivateAsync();
        Directory.Delete(Folder("Beta"));

        await vm.CheckAgainAsync();

        vm.CheckMessage.ShouldBe("Checked 2 notes. 1 newly orphaned.");
        vm.Items.Single().Title.ShouldBe("Beta");

        await vm.CheckAgainAsync();
        vm.CheckMessage.ShouldBe("Checked 2 notes. Nothing changed.");
    }

    [Fact]
    public void REQ061_FormatReport()
    {
        OrphansViewModel.FormatReport(new OrphanScanReport(1, 0, 0, 0)).ShouldBe("Checked 1 note. Nothing changed.");
        OrphansViewModel.FormatReport(new OrphanScanReport(12, 1, 2, 1, Conflicts: 1))
            .ShouldBe("Checked 12 notes. 2 reattached, 2 newly orphaned, 1 conflict.");
    }

    // ───────────── relink ─────────────

    [Fact(Timeout = 20_000)]
    public async Task REQ061_Relink_Cancelled_ChangesNothing_AndStartsAtTheNearestExistingParent()
    {
        var item = await OrphanAsync(@"Clients\Acme");
        using var vm = await OpenAsync();
        _picker.Answer = null;

        await vm.RelinkAsync(vm.Items.Single());

        _picker.StartFolders.Single().ShouldBe(Path.Combine(Root, "Clients"));
        _picker.CommitTexts.Single().ShouldBe("Relink");
        vm.Items.Single().Error.ShouldBeNull();
        (await _store.GetByIdAsync(item.Id, Ct)).ShouldBeEquivalentTo(item);
    }

    [Fact(Timeout = 20_000)]
    public async Task REQ061_Relink_ToAFile_OrAMissingFolder_OrTheRecycleBin_IsRefused()
    {
        var item = await OrphanAsync("Acme");
        using var vm = await OpenAsync();
        var row = vm.Items.Single();
        var file = Path.Combine(Root, "notes.txt");
        await File.WriteAllTextAsync(file, "x", Ct);
        var binned = Directory.CreateDirectory(Path.Combine(Root, "$RECYCLE.BIN", "S-1-5", "$R1")).FullName;

        _picker.Answer = file;
        await vm.RelinkAsync(row);
        row.Error.ShouldBe("That isn't a folder.");

        _picker.Answer = Path.Combine(Root, "Nowhere");
        await vm.RelinkAsync(row);
        row.Error.ShouldBe("That isn't a folder.");

        _picker.Answer = binned;
        await vm.RelinkAsync(row);
        row.Error.ShouldBe("That folder is in the Recycle Bin. Restore it first.");

        _confirms.ShouldBeEmpty();
        (await _store.GetByIdAsync(item.Id, Ct)).ShouldBeEquivalentTo(item);
    }

    [Fact(Timeout = 20_000)]
    public async Task REQ061_Relink_ToADifferentlyNamedFolder_AsksFirst_CancelChangesNothing()
    {
        var item = await OrphanAsync("Acme", "Client: Acme");
        var other = Directory.CreateDirectory(Path.Combine(Root, "Invoices")).FullName;
        using var vm = await OpenAsync();
        _picker.Answer = other;
        _confirmAnswer = false;

        await vm.RelinkAsync(vm.Items.Single());

        _confirms.Single().ShouldBe(("Relink to a different folder?",
            $"'Client: Acme' was for 'Acme'. You picked 'Invoices' ({_identity.Resolve(other).PathDisplay})."));
        (await _store.GetByIdAsync(item.Id, Ct)).ShouldBeEquivalentTo(item);
        vm.Items.Count.ShouldBe(1);

        _confirmAnswer = true;
        await vm.RelinkAsync(vm.Items.Single());
        (await _store.GetByIdAsync(item.Id, Ct))!.Folder.FileId.ShouldBe(_identity.Resolve(other).FileId);
        vm.Items.ShouldBeEmpty();
    }

    [Fact(Timeout = 20_000)]
    public async Task REQ061_Relink_SameName_NoQuestion_ThenUndo_PutsEverythingBack()
    {
        var item = await OrphanAsync("Acme", "Client: Acme");
        var restored = Directory.CreateDirectory(Path.Combine(Root, "Restored", "ACME")).FullName; // same name, other case
        using var vm = await OpenAsync();
        _picker.Answer = restored;
        _clock.Advance(TimeSpan.FromMinutes(5));

        await vm.RelinkAsync(vm.Items.Single());

        _confirms.ShouldBeEmpty();
        var target = _identity.Resolve(restored);
        vm.BarText.ShouldBe($"Relinked 'Client: Acme' to {target.PathDisplay}.");
        vm.CanUndo.ShouldBeTrue();
        vm.Items.ShouldBeEmpty();
        var relinked = (await _store.GetByIdAsync(item.Id, Ct))!;
        relinked.Folder.ShouldBe(target);
        relinked.IsOrphaned.ShouldBeFalse();
        relinked.ShouldBeEquivalentTo(item with { Folder = target, OrphanedAt = null }); // content and timestamps untouched

        await vm.UndoAsync();

        var undone = (await _store.GetByIdAsync(item.Id, Ct))!;
        undone.Folder.ShouldBe(item.Folder);
        undone.IsOrphaned.ShouldBeTrue();
        undone.ShouldBeEquivalentTo(item with { OrphanedAt = undone.OrphanedAt }); // only the orphan time moves
        vm.BarText.ShouldBeNull();
        vm.Items.Single().Id.ShouldBe(item.Id);
    }

    [Fact(Timeout = 20_000)]
    public async Task REQ061_Relink_ToAFolderThatHasANote_IsRefused_WithTheOtherTitle()
    {
        var item = await OrphanAsync("Acme");
        var taken = await AddAsync(@"Elsewhere\Acme", "Acme (current)");
        using var vm = await OpenAsync();
        _picker.Answer = Folder(@"Elsewhere\Acme");

        await vm.RelinkAsync(vm.Items.Single());

        vm.Items.Single().Error.ShouldBe($"{taken.Folder.PathDisplay} already has a note ('Acme (current)'). Delete or relink that note first.");
        vm.CanUndo.ShouldBeFalse();
        (await _store.GetByIdAsync(item.Id, Ct)).ShouldBeEquivalentTo(item);
        (await _store.GetByIdAsync(taken.Id, Ct)).ShouldBeEquivalentTo(taken);
    }

    [Fact(Timeout = 20_000)]
    public async Task REQ061_Relink_WhenTheNoteChangedMeanwhile_IsRefused_AndKeepsTheNewerBinding()
    {
        var item = await OrphanAsync("Acme");
        var first = Directory.CreateDirectory(Path.Combine(Root, "First", "Acme")).FullName;
        var second = Directory.CreateDirectory(Path.Combine(Root, "Second", "Acme")).FullName;
        using var vm = await OpenAsync();
        _picker.Answer = second;
        _picker.WhilePicking = () => _store.RelinkAsync(item.Id, _identity.Resolve(first), item.Folder, Ct); // e.g. another window

        await vm.RelinkAsync(vm.Items.Single(i => i.Id == item.Id));

        (await _store.GetByIdAsync(item.Id, Ct))!.Folder.ShouldBe(_identity.Resolve(first));
        vm.CanUndo.ShouldBeFalse();
        vm.Items.ShouldBeEmpty(); // reloaded: it's attached now
    }

    [Fact(Timeout = 20_000)]
    public async Task REQ061_Relink_OfANoteDeletedMeanwhile_IsRefused()
    {
        var item = await OrphanAsync("Acme");
        var target = Directory.CreateDirectory(Path.Combine(Root, "New", "Acme")).FullName;
        using var vm = await OpenAsync();
        var row = vm.Items.Single();
        _picker.Answer = target;
        _picker.WhilePicking = () => _store.DeleteAsync(item.Id, Ct);

        await vm.RelinkAsync(row);

        row.Error.ShouldBe("This note changed in the meantime.");
        var now = (await _store.GetByIdAsync(item.Id, Ct))!;
        now.IsDeleted.ShouldBeTrue();
        now.Folder.ShouldBe(item.Folder);
    }

    [Fact(Timeout = 20_000)]
    public async Task REQ061_PickerFailure_IsShown_AndChangesNothing()
    {
        var item = await OrphanAsync("Acme");
        using var vm = await OpenAsync();
        _picker.Throw = new InvalidOperationException("no window");

        await vm.RelinkAsync(vm.Items.Single());

        vm.Items.Single().Error.ShouldBe("Couldn't open the folder picker: no window");
        (await _store.GetByIdAsync(item.Id, Ct)).ShouldBeEquivalentTo(item);
    }

    [Fact(Timeout = 20_000)]
    public async Task REQ061_Undo_FailsCleanly_WhenTheOldPathHasANewNote()
    {
        var item = await OrphanAsync("Acme");
        var target = Directory.CreateDirectory(Path.Combine(Root, "New", "Acme")).FullName;
        using var vm = await OpenAsync();
        _picker.Answer = target;
        await vm.RelinkAsync(vm.Items.Single());
        Directory.CreateDirectory(Folder("Acme"));
        await AddAsync("Acme", "Newcomer"); // a new note took the old path

        await vm.UndoAsync();

        vm.BarText.ShouldBe("Couldn't undo: that folder has another note now.");
        vm.CanUndo.ShouldBeFalse();
        var now = (await _store.GetByIdAsync(item.Id, Ct))!;
        now.Folder.ShouldBe(_identity.Resolve(target)); // still where the user put it
        now.IsOrphaned.ShouldBeFalse();
    }

    [Fact(Timeout = 20_000)]
    public async Task REQ061_UndoBar_ClosesAfter10Seconds()
    {
        await OrphanAsync("Acme");
        var target = Directory.CreateDirectory(Path.Combine(Root, "New", "Acme")).FullName;
        using var vm = await OpenAsync();
        _picker.Answer = target;
        await vm.RelinkAsync(vm.Items.Single());

        _clock.Advance(TimeSpan.FromSeconds(9));
        vm.CanUndo.ShouldBeTrue();
        _clock.Advance(TimeSpan.FromSeconds(1));
        vm.CanUndo.ShouldBeFalse();
        vm.BarText.ShouldBeNull();
    }

    // ───────────── delete / open ─────────────

    [Fact(Timeout = 20_000)]
    public async Task REQ006_REQ061_Delete_WithUndo_RestoresTheSameNote()
    {
        var item = await OrphanAsync("Acme", "Client: Acme");
        using var vm = await OpenAsync();

        await vm.DeleteAsync(vm.Items.Single());

        vm.BarText.ShouldBe("Deleted 'Client: Acme'.");
        vm.Items.ShouldBeEmpty();
        (await _store.GetByIdAsync(item.Id, Ct))!.IsDeleted.ShouldBeTrue();

        await vm.UndoAsync();
        (await _store.GetByIdAsync(item.Id, Ct)).ShouldBeEquivalentTo(item);
        vm.Items.Single().Id.ShouldBe(item.Id);
    }

    [Fact(Timeout = 20_000)]
    public async Task REQ061_OpenNote_AsksForTheLibrary()
    {
        var item = await OrphanAsync("Acme");
        using var vm = await OpenAsync();
        Guid? opened = null;
        vm.OpenNoteRequested += (_, id) => opened = id;

        vm.OpenNote(vm.Items.Single());

        opened.ShouldBe(item.Id);
    }

    [Fact(Timeout = 20_000)]
    public async Task REQ061_Badge_FollowsNoteChanges_BeforeThePageIsOpened()
    {
        var a = await AddAsync("Alpha", "Alpha");
        using var vm = Create();
        await vm.ReloadAsync();
        vm.Count.ShouldBe(0);

        await _store.SetOrphanedAsync(a.Id, true, null, Ct);
        _events.Raise(new NoteChangedEventArgs(a.Id, NoteChangeKind.OrphanStateChanged, a.Folder.PathNorm));
        await vm.WhenIdleAsync();

        vm.Count.ShouldBe(1);
    }

    // ───────────── helpers ─────────────

    private string Folder(string relative) => Path.Combine(Root, relative);

    private async Task<Note> AddAsync(string relative, string title)
    {
        var dir = Directory.CreateDirectory(Folder(relative)).FullName;
        return await _store.UpsertAsync(Note.CreateFor(_identity.Resolve(dir), _clock) with
        {
            Title = title,
            BodyMd = "Body of " + title,
            Labels = [Label.Create("client", "blue")],
            Status = NoteStatus.Keep,
            KeepUntil = new DateOnly(2027, 1, 1),
        }, Ct);
    }

    /// <summary>A note whose folder was deleted, after a scan.</summary>
    private async Task<Note> OrphanAsync(string relative, string? title = null)
    {
        var note = await AddAsync(relative, title ?? Path.GetFileName(relative));
        Directory.Delete(Folder(relative));
        (await new OrphanScanner(_store, _identity).CheckAsync(note, Ct)).Outcome.ShouldBe(OrphanOutcome.Orphaned);
        return (await _store.GetByIdAsync(note.Id, Ct))!;
    }

    private async Task<OrphansViewModel> OpenAsync()
    {
        var vm = Create();
        await vm.ActivateAsync();
        return vm;
    }

    private OrphansViewModel Create()
    {
        var scanner = new OrphanScanner(_store, _identity);
        return new OrphansViewModel(_store, scanner, () => scanner.ScanAsync(Ct), _identity, _events, new InlineDispatcher(), _clock,
            Logger.None, culture: CultureInfo.InvariantCulture, zone: TimeZoneInfo.Utc)
        {
            Picker = _picker,
            Confirm = (title, message) =>
            {
                _confirms.Add((title, message));
                return Task.FromResult(_confirmAnswer);
            },
        };
    }

    private sealed class FakePicker : IFolderPicker
    {
        public string? Answer { get; set; }
        public Func<Task>? WhilePicking { get; set; }
        public Exception? Throw { get; set; }
        public List<string?> StartFolders { get; } = [];
        public List<string> CommitTexts { get; } = [];

        public async Task<string?> PickFolderAsync(string? startFolder, string commitText)
        {
            StartFolders.Add(startFolder);
            CommitTexts.Add(commitText);
            if (Throw is { } ex)
            {
                throw ex;
            }

            if (WhilePicking is { } during)
            {
                await during();
            }

            return Answer;
        }
    }

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

        public event EventHandler? LabelsChanged
        {
            add { }
            remove { }
        }

        public void Raise(NoteChangedEventArgs e) => NoteChanged?.Invoke(this, e);
    }
}
