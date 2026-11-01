using FolderNotes.Core.Domain;
using FolderNotes.Core.Identity;
using FolderNotes.Core.Storage;

namespace FolderNotes.Core.Tests.Storage;

/// <summary>
/// REQ-060/061 data integrity (03 § Orphan detection, § Relink rules, ADR-020) with a fake identity,
/// for what one real disk can't show: another volume at the same letter, an unplugged drive, the
/// Recycle Bin, conflicts and races.
/// </summary>
public sealed class OrphanIntegrityTests : IAsyncLifetime
{
    private const string S1 = "00000000CAFEBABE"; // StoreFixture's default serial
    private const string S2 = "00000000DEADBEEF";
    private StoreFixture _f = null!;

    private SqliteNoteStore Store => _f.Store;
    private FakeIdentity Id => _f.Identity;
    private OrphanScanner Scanner => new(Store, Id, Id.Exists);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _f = await StoreFixture.CreateAsync();

    public ValueTask DisposeAsync() => _f.DisposeAsync();

    private static FolderRef On(string path, string fileId, string serial = S1) => StoreFixture.Folder(path, fileId, serial);

    private async Task<Note> AddAsync(string path, string fileId, string title = "Note", string serial = S1)
    {
        var folder = On(path, fileId, serial);
        Id.PutFolder(folder);
        Id.Place(fileId, LocateStatus.Found, folder.PathDisplay);
        return await Store.UpsertAsync(Note.CreateFor(folder, _f.Clock) with
        {
            Title = title,
            BodyMd = "Keep invoices until 2027.\n\n- [ ] ask Sam",
            Labels = [Label.Create("client", "blue")],
            Status = NoteStatus.Keep,
            AccentColor = "indigo",
            KeepUntil = new DateOnly(2027, 1, 1),
            ReviewOn = new DateOnly(2026, 12, 1),
        }, Ct);
    }

    private async Task<Note> ReadAsync(Guid id) => (await Store.GetByIdAsync(id, Ct))!;

    /// <summary>Everything the user wrote, and the timestamps, are untouched.</summary>
    private static void ShouldHaveSameContent(Note actual, Note expected)
    {
        actual.Id.ShouldBe(expected.Id);
        actual.Title.ShouldBe(expected.Title);
        actual.BodyMd.ShouldBe(expected.BodyMd);
        actual.Labels.ShouldBe(expected.Labels);
        actual.Status.ShouldBe(expected.Status);
        actual.AccentColor.ShouldBe(expected.AccentColor);
        actual.KeepUntil.ShouldBe(expected.KeepUntil);
        actual.ReviewOn.ShouldBe(expected.ReviewOn);
        actual.SafeAfter.ShouldBe(expected.SafeAfter);
        actual.TravelWithFolder.ShouldBe(expected.TravelWithFolder);
        actual.CreatedAt.ShouldBe(expected.CreatedAt);
        actual.UpdatedAt.ShouldBe(expected.UpdatedAt);
    }

    // ───────────── another volume at the same path / unplugged drive ─────────────

    [Fact]
    public async Task REQ061_DifferentDriveAtSameLetter_Scan_OrphansAsDriveUnavailable_AndKeepsTheIds()
    {
        var photos = await AddAsync(@"E:\Photos", "000000000000000000000000000000A1");
        Id.Place("000000000000000000000000000000A1", LocateStatus.VolumeNotMounted, null);
        Id.PutFolder(On(@"E:\Photos", "000000000000000000000000000000B2", S2)); // another USB drive, same path

        var result = await Scanner.CheckAsync(photos, Ct);

        result.Check.State.ShouldBe(FolderState.DriveUnavailable);
        result.Outcome.ShouldBe(OrphanOutcome.Orphaned);
        var now = await ReadAsync(photos.Id);
        now.Folder.ShouldBe(photos.Folder); // still bound to the original drive's folder
        now.IsOrphaned.ShouldBeTrue();
        ShouldHaveSameContent(now, photos);
    }

    [Fact]
    public async Task REQ061_DifferentVolumeAtSamePath_GetByFolder_ReturnsNoNote_AndRebindsNothing()
    {
        var photos = await AddAsync(@"E:\Photos", "000000000000000000000000000000A1");
        Id.Place("000000000000000000000000000000A1", LocateStatus.NotFound, null); // even if the ID looks gone

        var other = On(@"E:\Photos", "000000000000000000000000000000B2", S2);
        (await Store.GetByFolderAsync(other, Ct)).ShouldBeNull();

        (await ReadAsync(photos.Id)).Folder.ShouldBe(photos.Folder);
    }

    [Fact]
    public async Task REQ061_DriveReconnected_NextCheckRecoversTheNote()
    {
        var photos = await AddAsync(@"E:\Photos", "000000000000000000000000000000A1");
        Id.Place("000000000000000000000000000000A1", LocateStatus.VolumeNotMounted, null);
        Id.RemoveFolder(@"E:\Photos");
        (await Scanner.ScanAsync(Ct)).Orphaned.ShouldBe(1);

        Id.PutFolder(photos.Folder);
        Id.Place("000000000000000000000000000000A1", LocateStatus.Found, @"E:\Photos");
        var report = await Scanner.ScanAsync(Ct);

        report.Recovered.ShouldBe(1);
        var now = await ReadAsync(photos.Id);
        now.IsOrphaned.ShouldBeFalse();
        now.Folder.ShouldBe(photos.Folder);
    }

    [Fact]
    public async Task REQ062_PathOnlyNote_ShareOffline_IsDriveUnavailable_ThenRecovers()
    {
        var share = StoreFixture.Folder(@"\\nas\team\Projects");
        var note = await Store.UpsertAsync(Note.CreateFor(share, _f.Clock), Ct);

        Scanner.Classify(note).State.ShouldBe(FolderState.DriveUnavailable);
        (await Scanner.CheckAsync(note, Ct)).Outcome.ShouldBe(OrphanOutcome.Orphaned);

        Id.PutFolder(share);
        (await Scanner.CheckAsync(await ReadAsync(note.Id), Ct)).Outcome.ShouldBe(OrphanOutcome.Recovered);
        (await ReadAsync(note.Id)).Folder.HasFileId.ShouldBeFalse(); // still tracked by path only
    }

    [Fact]
    public async Task REQ061_PathOnlyNote_FolderGoneButShareThere_IsMissing()
    {
        var note = await Store.UpsertAsync(Note.CreateFor(StoreFixture.Folder(@"\\nas\team\Old"), _f.Clock), Ct);
        Id.PutRoot(@"\\nas\team");

        Scanner.Classify(note).State.ShouldBe(FolderState.Missing);
    }

    // ───────────── Recycle Bin ─────────────

    [Fact]
    public async Task REQ061_FolderInRecycleBin_IsOrphaned_NotRelinkedIntoTheBin()
    {
        var acme = await AddAsync(@"D:\Clients\Acme", "000000000000000000000000000000A1");
        Id.RemoveFolder(@"D:\Clients\Acme");
        Id.PutRoot(@"D:\");
        Id.Place("000000000000000000000000000000A1", LocateStatus.InRecycleBin, @"D:\$RECYCLE.BIN\S-1-5-21\$R1X2Y3");

        var result = await Scanner.CheckAsync(acme, Ct);

        result.Check.State.ShouldBe(FolderState.InRecycleBin);
        result.Check.Path.ShouldBe(@"D:\$RECYCLE.BIN\S-1-5-21\$R1X2Y3");
        result.Outcome.ShouldBe(OrphanOutcome.Orphaned);
        (await ReadAsync(acme.Id)).Folder.ShouldBe(acme.Folder);
    }

    [Fact]
    public async Task REQ061_RestoredFromRecycleBin_Recovers()
    {
        var acme = await AddAsync(@"D:\Clients\Acme", "000000000000000000000000000000A1");
        await Store.SetOrphanedAsync(acme.Id, true, null, Ct);
        // Restored: the same ID is back at the same path (Place says Found there).

        (await Scanner.CheckAsync(await ReadAsync(acme.Id), Ct)).Outcome.ShouldBe(OrphanOutcome.Recovered);
    }

    [Fact]
    public async Task REQ060_RecreatedAtSamePath_SameVolume_Rebinds_EvenIfOriginalInRecycleBin()
    {
        var bin = await AddAsync(@"D:\Build\bin", "000000000000000000000000000000A1");
        Id.Place("000000000000000000000000000000A1", LocateStatus.InRecycleBin, @"D:\$RECYCLE.BIN\S-1\$R9");
        Id.PutFolder(On(@"D:\Build\bin", "000000000000000000000000000000A2"));

        var result = await Scanner.CheckAsync(bin, Ct);

        result.Check.State.ShouldBe(FolderState.Recreated);
        result.Outcome.ShouldBe(OrphanOutcome.Relinked);
        (await ReadAsync(bin.Id)).Folder.FileId.ShouldBe("000000000000000000000000000000A2");
    }

    // ───────────── conflicts: never take over another note's folder ─────────────

    [Fact]
    public async Task REQ061_MovedOntoAnotherNotesPath_NeitherNoteChanges_BothOrphanedWithTheConflict()
    {
        // A's folder moved to D:\B, where B's folder used to be (B's folder was deleted).
        var a = await AddAsync(@"D:\A", "000000000000000000000000000000A1", "Alpha");
        var b = await AddAsync(@"D:\B", "000000000000000000000000000000B1", "Beta");
        Id.RemoveFolder(@"D:\A");
        Id.Place("000000000000000000000000000000B1", LocateStatus.NotFound, null);
        Id.PutFolder(On(@"D:\B", "000000000000000000000000000000A1"));
        Id.Place("000000000000000000000000000000A1", LocateStatus.Found, @"D:\B");

        var ra = await Scanner.CheckAsync(a, Ct);
        var rb = await Scanner.CheckAsync(b, Ct);

        ra.Check.State.ShouldBe(FolderState.Moved);
        ra.Outcome.ShouldBe(OrphanOutcome.Conflict);
        ra.ConflictingNoteId.ShouldBe(b.Id);
        rb.Check.State.ShouldBe(FolderState.Recreated); // something is at B's path on the same volume...
        rb.Outcome.ShouldBe(OrphanOutcome.Conflict); // ...but its ID belongs to A's note
        rb.ConflictingNoteId.ShouldBe(a.Id);

        var nowA = await ReadAsync(a.Id);
        var nowB = await ReadAsync(b.Id);
        nowA.Folder.ShouldBe(a.Folder);
        nowB.Folder.ShouldBe(b.Folder);
        nowA.IsOrphaned.ShouldBeTrue();
        nowB.IsOrphaned.ShouldBeTrue();
        ShouldHaveSameContent(nowA, a);
        ShouldHaveSameContent(nowB, b);

        // The user deletes B's note: the next check moves A's note to its folder.
        await Store.DeleteAsync(b.Id, Ct);
        (await Scanner.CheckAsync(nowA, Ct)).Outcome.ShouldBe(OrphanOutcome.Relinked);
        (await ReadAsync(a.Id)).Folder.PathDisplay.ShouldBe(@"D:\B");
    }

    [Fact]
    public async Task REQ061_ScanReport_CountsConflicts()
    {
        await AddAsync(@"D:\A", "000000000000000000000000000000A1");
        await AddAsync(@"D:\B", "000000000000000000000000000000B1");
        Id.RemoveFolder(@"D:\A");
        Id.Place("000000000000000000000000000000B1", LocateStatus.NotFound, null);
        Id.PutFolder(On(@"D:\B", "000000000000000000000000000000A1"));
        Id.Place("000000000000000000000000000000A1", LocateStatus.Found, @"D:\B");

        (await Scanner.ScanAsync(Ct)).ShouldBe(new OrphanScanReport(Checked: 2, Relinked: 0, Orphaned: 0, Recovered: 0, Conflicts: 2));
    }

    // ───────────── races: the newer binding wins ─────────────

    [Fact]
    public async Task REQ061_StaleScanDecision_AfterAUserRelink_IsSkipped()
    {
        var a = await AddAsync(@"D:\A", "000000000000000000000000000000A1");
        Id.RemoveFolder(@"D:\A");
        Id.Place("000000000000000000000000000000A1", LocateStatus.NotFound, null);
        var picked = On(@"D:\Elsewhere\A", "000000000000000000000000000000C3");
        await Store.RelinkAsync(a.Id, picked, a.Folder, Ct); // the user relinked after the scan read `a`

        var result = await Scanner.CheckAsync(a, Ct); // the scan still holds the old copy

        result.Outcome.ShouldBe(OrphanOutcome.Skipped);
        var now = await ReadAsync(a.Id);
        now.Folder.ShouldBe(picked);
        now.IsOrphaned.ShouldBeFalse();
    }

    [Fact]
    public async Task REQ061_Relink_WithStaleExpectedBinding_Throws_AndChangesNothing()
    {
        var a = await AddAsync(@"D:\A", "000000000000000000000000000000A1");
        await Store.RelinkAsync(a.Id, On(@"D:\A2", "000000000000000000000000000000A2"), a.Folder, Ct);
        var before = await ReadAsync(a.Id);

        await Should.ThrowAsync<NoteBindingChangedException>(
            () => Store.RelinkAsync(a.Id, On(@"D:\A3", "000000000000000000000000000000A3"), a.Folder, Ct));
        await Should.ThrowAsync<NoteBindingChangedException>(() => Store.SetOrphanedAsync(a.Id, true, a.Folder, Ct));

        (await ReadAsync(a.Id)).ShouldBeEquivalentTo(before);
    }

    [Fact]
    public async Task REQ061_RelinkOrOrphan_OfADeletedNote_IsRefused()
    {
        var a = await AddAsync(@"D:\A", "000000000000000000000000000000A1");
        await Store.DeleteAsync(a.Id, Ct);

        await Should.ThrowAsync<NoteBindingChangedException>(() => Store.RelinkAsync(a.Id, On(@"D:\X", "000000000000000000000000000000F1"), a.Folder, Ct));
        await Should.ThrowAsync<KeyNotFoundException>(() => Store.RelinkAsync(a.Id, On(@"D:\X", "000000000000000000000000000000F1"), null, Ct));

        // A scan that read the note before the delete and now finds its folder gone skips it.
        Id.RemoveFolder(@"D:\A");
        Id.Place("000000000000000000000000000000A1", LocateStatus.NotFound, null);
        (await Scanner.CheckAsync(a, Ct)).Outcome.ShouldBe(OrphanOutcome.Skipped);
        (await ReadAsync(a.Id)).Folder.ShouldBe(a.Folder);
    }

    [Fact]
    public async Task REQ061_Relink_ToAFolderWhoseIdHasANote_IsDuplicate_AndChangesNothing()
    {
        var a = await AddAsync(@"D:\A", "000000000000000000000000000000A1");
        var b = await AddAsync(@"D:\B", "000000000000000000000000000000B1");

        var ex = await Should.ThrowAsync<DuplicateFolderNoteException>(
            () => Store.RelinkAsync(a.Id, On(@"D:\B renamed", "000000000000000000000000000000B1"), a.Folder, Ct));

        ex.ExistingNoteId.ShouldBe(b.Id);
        (await ReadAsync(a.Id)).Folder.ShouldBe(a.Folder);
        (await ReadAsync(b.Id)).Folder.ShouldBe(b.Folder);
    }

    // ───────────── an editor can't undo a relink ─────────────

    [Fact]
    public async Task REQ061_AutosaveFromAStaleEditorCopy_KeepsTheRelinkedFolder_AndOrphanState()
    {
        var a = await AddAsync(@"D:\A", "000000000000000000000000000000A1");
        var editorCopy = a; // an editor opened before the relink
        var picked = On(@"D:\New home", "000000000000000000000000000000C3");
        await Store.RelinkAsync(a.Id, picked, a.Folder, Ct);

        var saved = await Store.UpsertAsync(editorCopy with { Title = "Edited title" }, Ct);

        saved.Folder.ShouldBe(picked);
        var now = await ReadAsync(a.Id);
        now.Title.ShouldBe("Edited title");
        now.Folder.ShouldBe(picked);

        // Same for the orphan flag: an editor's copy without it doesn't clear it.
        await Store.SetOrphanedAsync(a.Id, true, picked, Ct);
        await Store.UpsertAsync(editorCopy with { Title = "Edited again" }, Ct);
        (await ReadAsync(a.Id)).IsOrphaned.ShouldBeTrue();
    }

    // ───────────── content and metadata survive relink and undo ─────────────

    [Fact]
    public async Task REQ061_RelinkAndUndo_KeepEveryFieldTheUserWrote_AndUpdatedAt()
    {
        var a = await AddAsync(@"D:\A", "000000000000000000000000000000A1");
        await Store.SetOrphanedAsync(a.Id, true, a.Folder, Ct);
        _f.Clock.Advance(TimeSpan.FromHours(3));
        var orphan = await ReadAsync(a.Id);

        var picked = On(@"D:\Found it", "000000000000000000000000000000C3");
        var relinked = await Store.RelinkAsync(a.Id, picked, orphan.Folder, Ct);
        ShouldHaveSameContent(relinked, a);
        relinked.IsOrphaned.ShouldBeFalse();

        // Undo (05 § Orphaned page): back to the old binding, orphaned again.
        await Store.RelinkAsync(a.Id, orphan.Folder, picked, Ct);
        await Store.SetOrphanedAsync(a.Id, true, orphan.Folder, Ct);
        var undone = await ReadAsync(a.Id);
        ShouldHaveSameContent(undone, a);
        undone.Folder.ShouldBe(a.Folder);
        undone.IsOrphaned.ShouldBeTrue();
    }

    [Fact]
    public async Task REQ061_Scan_NeverChangesContent_OrUpdatedAt()
    {
        var moved = await AddAsync(@"D:\M", "000000000000000000000000000000A1");
        var gone = await AddAsync(@"D:\G", "000000000000000000000000000000B1");
        Id.Place("000000000000000000000000000000A1", LocateStatus.Found, @"D:\M2");
        Id.PutFolder(On(@"D:\M2", "000000000000000000000000000000A1"));
        Id.RemoveFolder(@"D:\G");
        Id.Place("000000000000000000000000000000B1", LocateStatus.NotFound, null);
        _f.Clock.Advance(TimeSpan.FromDays(1));

        await Scanner.ScanAsync(Ct);

        ShouldHaveSameContent(await ReadAsync(moved.Id), moved);
        ShouldHaveSameContent(await ReadAsync(gone.Id), gone);
    }

    // ───────────── RelinkTarget (03 § Relink rules) ─────────────

    [Fact]
    public async Task REQ061_RelinkTarget_RefusesRecycleBin_AndFlagsADifferentName()
    {
        var a = await AddAsync(@"D:\Clients\Acme", "000000000000000000000000000000A1");
        Id.PutFolder(On(@"D:\$RECYCLE.BIN\S-1\$R1", "000000000000000000000000000000E1"));
        Id.PutFolder(On(@"D:\Archive\ACME", "000000000000000000000000000000E2"));
        Id.PutFolder(On(@"D:\Archive\Invoices", "000000000000000000000000000000E3"));

        RelinkTarget.Check(Id, a, @"D:\$RECYCLE.BIN\S-1\$R1", Id.Exists).Problem.ShouldBe(RelinkProblem.InRecycleBin);
        RelinkTarget.Check(Id, a, @"D:\Nowhere", Id.Exists).Problem.ShouldBe(RelinkProblem.NotAFolder);
        RelinkTarget.Check(Id, a, "  ", Id.Exists).Problem.ShouldBe(RelinkProblem.NotAFolder);
        RelinkTarget.Check(Id, a, "relative\\path", Id.Exists).Problem.ShouldBe(RelinkProblem.NotAFolder);

        var sameName = RelinkTarget.Check(Id, a, @"D:\Archive\ACME", Id.Exists);
        sameName.Problem.ShouldBe(RelinkProblem.None);
        sameName.NameDiffers.ShouldBeFalse(); // ignoring case
        sameName.Target!.FileId.ShouldBe("000000000000000000000000000000E2");

        RelinkTarget.Check(Id, a, @"D:\Archive\Invoices", Id.Exists).NameDiffers.ShouldBeTrue();
        RelinkTarget.Check(Id, a, @"D:\Clients\Acme", Id.Exists).SameFolder.ShouldBeTrue();
    }
}
