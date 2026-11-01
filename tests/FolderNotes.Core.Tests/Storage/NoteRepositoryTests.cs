using FolderNotes.Core.Domain;
using FolderNotes.Core.Identity;
using FolderNotes.Core.Storage;
using Microsoft.Data.Sqlite;

namespace FolderNotes.Core.Tests.Storage;

public sealed class NoteRepositoryTests : IAsyncLifetime
{
    private StoreFixture _f = null!;

    private SqliteNoteStore Store => _f.Store;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _f = await StoreFixture.CreateAsync();

    public ValueTask DisposeAsync() => _f.DisposeAsync();

    private Note Full(string path = @"C:\Clients\Acme") => _f.NewNote(path, "Client: Acme Ltd", fileId: "0000000000000000000000000000ACE1") with
    {
        BodyMd = "# Retention\n\n- [ ] keep invoices\n\nÜnïcödé ✓ 📁",
        Labels = [Label.Create("client", "blue"), Label.Create("Zeta new", "teal")],
        Status = NoteStatus.Keep,
        AccentColor = "indigo",
        KeepUntil = new DateOnly(2027, 1, 1),
        ReviewOn = new DateOnly(2026, 12, 1),
        SafeAfter = new DateOnly(2027, 6, 30),
        TravelWithFolder = true,
    };

    // ── schema ──

    [Fact]
    public async Task REQ013_NewDatabase_HasStarterLabels()
    {
        var labels = await Store.ListLabelsAsync(Ct);
        labels.Select(l => l.Name).ShouldBe(["archive", "build-output", "client", "do-not-delete", "personal"]);
    }

    [Fact]
    public async Task Schema_IsVersion1_InWalMode()
    {
        await using var c = new SqliteConnection($"Data Source={_f.DbPath};Mode=ReadOnly;Pooling=False");
        await c.OpenAsync(Ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA user_version";
        Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture).ShouldBe(1);
        cmd.CommandText = "PRAGMA journal_mode";
        ((string)cmd.ExecuteScalar()!).ShouldBe("wal");
    }

    [Fact]
    public async Task Schema_Reopen_DoesNotReseedOrBackup()
    {
        await Store.CreateLabelAsync("extra", "pink", Ct);
        await _f.ReopenAsync();

        (await Store.ListLabelsAsync(Ct)).Count.ShouldBe(6);
        Directory.Exists(Path.Combine(_f.Directory, "backups")).ShouldBeFalse();
    }

    // ── create / read ──

    [Fact]
    public async Task REQ002_Upsert_RoundTripsEveryField()
    {
        var saved = await Store.UpsertAsync(Full(), Ct);
        var loaded = await Store.GetByIdAsync(saved.Id, Ct);

        loaded.ShouldNotBeNull();
        loaded.ShouldBeEquivalentTo(saved);
        loaded.Labels.Select(l => l.Name).ShouldBe(["client", "Zeta new"]);
        loaded.Labels.Single(l => l.Name == "client").Color.ShouldBe("blue");
        loaded.Folder.HasFileId.ShouldBeTrue();
    }

    [Fact]
    public async Task REQ005_Upsert_StampsUpdatedAt_KeepsCreatedAt()
    {
        var first = await Store.UpsertAsync(_f.NewNote(@"C:\A"), Ct);
        _f.Clock.Advance(TimeSpan.FromMinutes(5));
        var second = await Store.UpsertAsync(first with { Title = "Edited", CreatedAt = DateTimeOffset.UnixEpoch }, Ct);

        second.CreatedAt.ShouldBe(first.CreatedAt);
        second.UpdatedAt.ShouldBe(first.CreatedAt.AddMinutes(5));
        (await Store.GetByIdAsync(first.Id, Ct))!.Title.ShouldBe("Edited");
    }

    [Fact]
    public async Task REQ002_InvalidNote_IsRejected_AndNothingIsWritten()
    {
        var bad = _f.NewNote(@"C:\Bad") with { Title = new string('x', 121) };
        await Should.ThrowAsync<NoteValidationException>(() => Store.UpsertAsync(bad, Ct));
        (await Store.GetByIdAsync(bad.Id, Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task REQ001_SecondNoteForSamePath_IsDuplicate()
    {
        var first = await Store.UpsertAsync(_f.NewNote(@"C:\Clients\Acme"), Ct);
        var ex = await Should.ThrowAsync<DuplicateFolderNoteException>(() => Store.UpsertAsync(_f.NewNote(@"c:\clients\ACME\"), Ct));
        ex.ExistingNoteId.ShouldBe(first.Id);
    }

    [Fact]
    public async Task REQ001_SecondNoteForSameFileId_IsDuplicate()
    {
        var first = await Store.UpsertAsync(_f.NewNote(@"C:\A", fileId: "00000000000000000000000000000001"), Ct);
        var ex = await Should.ThrowAsync<DuplicateFolderNoteException>(() =>
            Store.UpsertAsync(_f.NewNote(@"C:\B", fileId: "00000000000000000000000000000001"), Ct));
        ex.ExistingNoteId.ShouldBe(first.Id);
    }

    [Fact]
    public async Task REQ001_SameFileIdOnOtherVolume_IsNotDuplicate()
    {
        await Store.UpsertAsync(_f.NewNote(@"C:\A", fileId: "00000000000000000000000000000001"), Ct);
        var other = Note.CreateFor(StoreFixture.Folder(@"D:\A", "00000000000000000000000000000001", serial: "0000000000000D0D"), _f.Clock);
        await Should.NotThrowAsync(() => Store.UpsertAsync(other, Ct));
    }

    // ── delete / restore / purge ──

    [Fact]
    public async Task REQ006_Delete_ThenRestore_GivesIdenticalNote()
    {
        var saved = await Store.UpsertAsync(Full(), Ct);
        await Store.DeleteAsync(saved.Id, Ct);

        (await Store.GetByIdAsync(saved.Id, Ct))!.IsDeleted.ShouldBeTrue();
        (await Store.ListAsync(NoteQuery.All, Ct)).ShouldBeEmpty();
        (await Store.GetByFolderAsync(saved.Folder, Ct)).ShouldBeNull();
        (await Store.ListAsync(new NoteQuery { State = NoteListState.Deleted }, Ct)).Single().Id.ShouldBe(saved.Id);

        await Store.RestoreAsync(saved.Id, Ct);
        var restored = await Store.GetByIdAsync(saved.Id, Ct);
        restored.ShouldBeEquivalentTo(saved);
    }

    [Fact]
    public async Task REQ006_DeletedFolder_CanGetANewNote_ThenRestoreConflicts()
    {
        var old = await Store.UpsertAsync(_f.NewNote(@"C:\A", "old"), Ct);
        await Store.DeleteAsync(old.Id, Ct);
        var replacement = await Store.UpsertAsync(_f.NewNote(@"C:\A", "new"), Ct);

        var ex = await Should.ThrowAsync<DuplicateFolderNoteException>(() => Store.RestoreAsync(old.Id, Ct));
        ex.ExistingNoteId.ShouldBe(replacement.Id);
    }

    [Fact]
    public async Task REQ006_EditingADeletedNote_Fails()
    {
        var n = await Store.UpsertAsync(_f.NewNote(@"C:\A"), Ct);
        await Store.DeleteAsync(n.Id, Ct);
        await Should.ThrowAsync<InvalidOperationException>(() => Store.UpsertAsync(n with { Title = "x" }, Ct));
    }

    [Fact]
    public async Task REQ006_DeleteAndRestore_AreIdempotent_MissingThrows()
    {
        var n = await Store.UpsertAsync(_f.NewNote(@"C:\A"), Ct);
        await Store.DeleteAsync(n.Id, Ct);
        await Store.DeleteAsync(n.Id, Ct);
        await Store.RestoreAsync(n.Id, Ct);
        await Store.RestoreAsync(n.Id, Ct);
        await Should.ThrowAsync<KeyNotFoundException>(() => Store.DeleteAsync(Guid.NewGuid(), Ct));
        await Should.ThrowAsync<KeyNotFoundException>(() => Store.RestoreAsync(Guid.NewGuid(), Ct));
    }

    [Fact]
    public async Task REQ006_Purge_RemovesOnlyNotesDeletedLongEnoughAgo()
    {
        var a = await Store.UpsertAsync(_f.NewNote(@"C:\A"), Ct);
        var b = await Store.UpsertAsync(_f.NewNote(@"C:\B"), Ct);
        var c = await Store.UpsertAsync(_f.NewNote(@"C:\C"), Ct);
        await Store.DeleteAsync(a.Id, Ct);
        _f.Clock.Advance(TimeSpan.FromDays(20));
        await Store.DeleteAsync(b.Id, Ct);
        _f.Clock.Advance(TimeSpan.FromDays(11));

        (await Store.PurgeDeletedAsync(TimeSpan.FromDays(30), Ct)).ShouldBe(1);
        (await Store.GetByIdAsync(a.Id, Ct)).ShouldBeNull();
        (await Store.GetByIdAsync(b.Id, Ct))!.IsDeleted.ShouldBeTrue();
        (await Store.GetByIdAsync(c.Id, Ct))!.IsDeleted.ShouldBeFalse();
    }

    // ── labels ──

    [Fact]
    public async Task REQ011_Upsert_CreatesUnknownLabels_AndReusesKnownOnesIgnoringCase()
    {
        var n = await Store.UpsertAsync(_f.NewNote(@"C:\A") with { Labels = [Label.Create("CLIENT", "red"), Label.Create("brand-new", "lime")] }, Ct);

        n.Labels.Single(l => l.Name == "client").Color.ShouldBe("blue"); // the existing label wins
        (await Store.ListLabelsAsync(Ct)).Select(l => l.Name).ShouldContain("brand-new");
        (await Store.ListLabelsAsync(Ct)).Count(l => Label.NameComparer.Equals(l.Name, "client")).ShouldBe(1);
    }

    [Fact]
    public async Task REQ010_CreateLabel_DuplicateNameIgnoringCase_Throws()
    {
        await Should.ThrowAsync<DuplicateLabelException>(() => Store.CreateLabelAsync("Client", "red", Ct));
        await Should.ThrowAsync<NoteValidationException>(() => Store.CreateLabelAsync("ok", "brown", Ct));
    }

    [Fact]
    public async Task REQ012_RenameLabel_UpdatesEveryNote()
    {
        var a = await Store.UpsertAsync(_f.NewNote(@"C:\A") with { Labels = [Label.Create("client", "blue")] }, Ct);
        var b = await Store.UpsertAsync(_f.NewNote(@"C:\B") with { Labels = [Label.Create("client", "blue")] }, Ct);
        var label = a.Labels.Single();

        await Store.UpdateLabelAsync(label with { Name = "customer", Color = "orange" }, Ct);

        foreach (var id in new[] { a.Id, b.Id })
        {
            var l = (await Store.GetByIdAsync(id, Ct))!.Labels.Single();
            (l.Name, l.Color).ShouldBe(("customer", "orange"));
        }
    }

    [Fact]
    public async Task REQ012_RenameLabel_ToExistingName_Throws()
    {
        var labels = await Store.ListLabelsAsync(Ct);
        var client = labels.Single(l => l.Name == "client");
        await Should.ThrowAsync<DuplicateLabelException>(() => Store.UpdateLabelAsync(client with { Name = "ARCHIVE" }, Ct));
        await Should.NotThrowAsync(() => Store.UpdateLabelAsync(client with { Name = "Client" }, Ct)); // case change of itself is fine
        await Should.ThrowAsync<KeyNotFoundException>(() => Store.UpdateLabelAsync(new Label(Guid.NewGuid(), "x", "red"), Ct));
    }

    [Fact]
    public async Task REQ012_DeleteLabel_RemovesItFromNotes()
    {
        var n = await Store.UpsertAsync(_f.NewNote(@"C:\A") with { Labels = [Label.Create("client", "blue"), Label.Create("personal", "green")] }, Ct);
        await Store.DeleteLabelAsync(n.Labels.First(l => l.Name == "client").Id, Ct);

        (await Store.GetByIdAsync(n.Id, Ct))!.Labels.Select(l => l.Name).ShouldBe(["personal"]);
        (await Store.ListLabelsAsync(Ct)).ShouldNotContain(l => l.Name == "client");
        await Should.ThrowAsync<KeyNotFoundException>(() => Store.DeleteLabelAsync(Guid.NewGuid(), Ct));
    }

    [Fact]
    public async Task REQ012_StaleLabelAfterRename_ResolvesById()
    {
        var n = await Store.UpsertAsync(_f.NewNote(@"C:\A") with { Labels = [Label.Create("client", "blue")] }, Ct);
        var stale = n.Labels.Single();
        await Store.UpdateLabelAsync(stale with { Name = "customer" }, Ct);

        var again = await Store.UpsertAsync(n with { Title = "edited", Labels = [stale] }, Ct);
        again.Labels.Single().Name.ShouldBe("customer");
        (await Store.ListLabelsAsync(Ct)).ShouldNotContain(l => l.Name == "client");
    }

    // ── list / filter / sort (REQ-054, REQ-052) ──

    [Fact]
    public async Task REQ054_List_FiltersAndSorts()
    {
        var acme = await Store.UpsertAsync(_f.NewNote(@"D:\Clients\Acme", "Acme") with { Status = NoteStatus.Keep, Labels = [Label.Create("client", "blue")], KeepUntil = new(2027, 1, 1) }, Ct);
        _f.Clock.Advance(TimeSpan.FromSeconds(1));
        var beta = await Store.UpsertAsync(_f.NewNote(@"D:\Clients\Beta", "beta") with { Status = NoteStatus.Review, Labels = [Label.Create("client", "blue"), Label.Create("personal", "green")], ReviewOn = new(2026, 11, 15) }, Ct);
        _f.Clock.Advance(TimeSpan.FromSeconds(1));
        var bin = await Store.UpsertAsync(_f.NewNote(@"C:\Dev\app\bin", "bin") with { Status = NoteStatus.SafeToDelete, SafeAfter = new(2026, 10, 1) }, Ct);
        _f.Clock.Advance(TimeSpan.FromSeconds(1));
        await Store.UpsertAsync(_f.NewNote(@"D:\ClientsOld", "old"), Ct);

        async Task<IEnumerable<string>> Titles(NoteQuery q) => (await Store.ListAsync(q, Ct)).Select(n => n.Title);

        (await Titles(new NoteQuery { Sort = NoteSort.UpdatedDesc })).ShouldBe(["old", "bin", "beta", "Acme"]);
        (await Titles(new NoteQuery { Sort = NoteSort.Title })).ShouldBe(["Acme", "beta", "bin", "old"]);
        (await Titles(new NoteQuery { Sort = NoteSort.Path })).ShouldBe(["bin", "Acme", "beta", "old"]); // hierarchical (O-9)
        (await Titles(new NoteQuery { Sort = NoteSort.DueDate })).ShouldBe(["bin", "beta", "Acme", "old"]);
        (await Titles(new NoteQuery { Labels = ["CLIENT"], Sort = NoteSort.Title })).ShouldBe(["Acme", "beta"]);
        (await Titles(new NoteQuery { Labels = ["client", "personal"] })).ShouldBe(["beta"]);
        (await Titles(new NoteQuery { Labels = ["no-such-label"] })).ShouldBeEmpty();
        (await Titles(new NoteQuery { Statuses = [NoteStatus.Keep, NoteStatus.SafeToDelete], Sort = NoteSort.Title })).ShouldBe(["Acme", "bin"]);
        (await Titles(new NoteQuery { PathsUnder = [@"D:\CLIENTS"], Sort = NoteSort.Title })).ShouldBe(["Acme", "beta"]); // not ClientsOld
        (await Titles(new NoteQuery { PathsUnder = ["D:"], Sort = NoteSort.Title })).ShouldBe(["Acme", "beta", "old"]);
        (await Titles(new NoteQuery { Due = new DateRange(null, new DateOnly(2026, 10, 30)) })).ShouldBe(["bin"]);
        (await Titles(new NoteQuery { Due = new DateRange(new DateOnly(2026, 10, 31), new DateOnly(2026, 11, 30)) })).ShouldBe(["beta"]);
        (await Titles(new NoteQuery { Keep = new DateRange(null, new DateOnly(2027, 12, 31)) })).ShouldBe(["Acme"]);
        (await Titles(new NoteQuery { Sort = NoteSort.Title, Limit = 2, Offset = 1 })).ShouldBe(["beta", "bin"]);
        (await Titles(new NoteQuery { Ids = [acme.Id, bin.Id], Sort = NoteSort.Title })).ShouldBe(["Acme", "bin"]);
        _ = beta;
    }

    [Fact]
    public async Task REQ054_PathSort_IsHierarchical()
    {
        foreach (var path in new[] { @"D:\ClientsOld", @"D:\Clients-x", @"D:\Clients\Acme\2025", @"D:\Clients", @"D:\Clients\Acme", @"C:\Z", @"\\server\share\a" })
        {
            await Store.UpsertAsync(_f.NewNote(path, path), Ct);
        }

        (await Store.ListAsync(new NoteQuery { Sort = NoteSort.Path }, Ct)).Select(n => n.Title).ShouldBe(
        [
            @"\\server\share\a", // UNC paths start with '\', so they come first
            @"C:\Z",
            @"D:\Clients",
            @"D:\Clients\Acme",
            @"D:\Clients\Acme\2025", // subfolders right after their parent (O-9)
            @"D:\Clients-x",
            @"D:\ClientsOld",
        ]);
    }

    [Fact]
    public async Task REQ061_OrphanedState_IsListedSeparately()
    {
        var a = await Store.UpsertAsync(_f.NewNote(@"C:\A"), Ct);
        await Store.UpsertAsync(_f.NewNote(@"C:\B"), Ct);
        await Store.SetOrphanedAsync(a.Id, true, Ct);

        (await Store.ListAsync(new NoteQuery { State = NoteListState.Orphaned }, Ct)).Single().Id.ShouldBe(a.Id);
        (await Store.ListAsync(NoteQuery.All, Ct)).Count.ShouldBe(2);
        (await Store.GetByIdAsync(a.Id, Ct))!.OrphanedAt.ShouldBe(_f.Clock.GetUtcNow());

        await Store.SetOrphanedAsync(a.Id, false, Ct);
        (await Store.ListAsync(new NoteQuery { State = NoteListState.Orphaned }, Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task REQ061_Relink_MovesNoteToNewFolder_AndClearsOrphan()
    {
        var a = await Store.UpsertAsync(_f.NewNote(@"C:\Old", fileId: "000000000000000000000000000000AA"), Ct);
        await Store.SetOrphanedAsync(a.Id, true, Ct);

        var relinked = await Store.RelinkAsync(a.Id, StoreFixture.Folder(@"E:\New", "000000000000000000000000000000BB"), Ct);

        relinked.Folder.PathDisplay.ShouldBe(@"E:\New");
        relinked.Folder.FileId.ShouldBe("000000000000000000000000000000BB");
        relinked.IsOrphaned.ShouldBeFalse();
        relinked.Title.ShouldBe(a.Title);
    }

    [Fact]
    public async Task REQ061_Relink_ToFolderThatHasANote_IsDuplicate()
    {
        var a = await Store.UpsertAsync(_f.NewNote(@"C:\A"), Ct);
        await Store.UpsertAsync(_f.NewNote(@"C:\B"), Ct);
        await Should.ThrowAsync<DuplicateFolderNoteException>(() => Store.RelinkAsync(a.Id, StoreFixture.Folder(@"C:\B"), Ct));
    }

    // ── ADR-009 lookup ──

    [Fact]
    public async Task REQ060_GetByFolder_SameFileIdAtNewPath_FollowsAndRebinds()
    {
        var a = await Store.UpsertAsync(_f.NewNote(@"C:\Clients\Acme", fileId: "00000000000000000000000000000AC1"), Ct);
        var moved = StoreFixture.Folder(@"C:\Archive\Acme Ltd", "00000000000000000000000000000AC1");
        var events = new List<NoteChangedEventArgs>();
        Store.NoteChanged += (_, e) => events.Add(e);

        var found = await Store.GetByFolderAsync(moved, Ct);

        found!.Id.ShouldBe(a.Id);
        found.Folder.PathDisplay.ShouldBe(@"C:\Archive\Acme Ltd");
        (await Store.GetByIdAsync(a.Id, Ct))!.Folder.PathNorm.ShouldBe(@"C:\ARCHIVE\ACME LTD");
        events.ShouldContain(e => e.Kind == NoteChangeKind.Relinked && e.NoteId == a.Id);
        (await Store.GetByFolderAsync(StoreFixture.Folder(@"C:\Clients\Acme"), Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task REQ060_GetByFolder_SamePathDifferentId_OriginalMovedAway_ReturnsNull()
    {
        await Store.UpsertAsync(_f.NewNote(@"C:\Work", fileId: "000000000000000000000000000000F1"), Ct);
        _f.Identity.Place("000000000000000000000000000000F1", LocateStatus.Found, @"C:\Elsewhere\Work");

        (await Store.GetByFolderAsync(StoreFixture.Folder(@"C:\Work", "000000000000000000000000000000F2"), Ct)).ShouldBeNull();
    }

    [Theory]
    [InlineData(LocateStatus.NotFound)]
    [InlineData(LocateStatus.InRecycleBin)]
    public async Task REQ060_GetByFolder_RecreatedAtSamePath_RebindsNote(LocateStatus original)
    {
        var a = await Store.UpsertAsync(_f.NewNote(@"C:\Dev\bin", "Build output", fileId: "000000000000000000000000000000B1"), Ct);
        _f.Identity.Place("000000000000000000000000000000B1", original, original == LocateStatus.InRecycleBin ? @"C:\$Recycle.Bin\S-1\$R1" : null);

        var found = await Store.GetByFolderAsync(StoreFixture.Folder(@"C:\Dev\bin", "000000000000000000000000000000B2"), Ct);

        found!.Id.ShouldBe(a.Id);
        found.Folder.FileId.ShouldBe("000000000000000000000000000000B2");
        (await Store.GetByFolderAsync(StoreFixture.Folder(@"C:\Dev\bin", "000000000000000000000000000000B2"), Ct))!.Id.ShouldBe(a.Id);
    }

    [Fact]
    public async Task REQ062_GetByFolder_PathOnly()
    {
        var a = await Store.UpsertAsync(_f.NewNote(@"\\nas\media\Movies"), Ct);
        (await Store.GetByFolderAsync(StoreFixture.Folder(@"\\NAS\Media\Movies\"), Ct))!.Id.ShouldBe(a.Id);
        (await Store.GetByFolderAsync(StoreFixture.Folder(@"\\nas\media\Music"), Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task REQ060_GetByFolder_PathNoteGainsFileIdWhenFolderAppears()
    {
        var a = await Store.UpsertAsync(_f.NewNote(@"C:\Later"), Ct); // folder didn't exist yet: no ID
        var found = await Store.GetByFolderAsync(StoreFixture.Folder(@"C:\Later", "000000000000000000000000000000C1"), Ct);
        found!.Id.ShouldBe(a.Id);
        found.Folder.FileId.ShouldBe("000000000000000000000000000000C1");
    }

    [Fact]
    public async Task REQ060_GetByFolder_IdMatchButNewPathTaken_ReturnsNoteWithoutRebinding()
    {
        var a = await Store.UpsertAsync(_f.NewNote(@"C:\A", fileId: "000000000000000000000000000000D1"), Ct);
        await Store.UpsertAsync(_f.NewNote(@"C:\B"), Ct);

        var found = await Store.GetByFolderAsync(StoreFixture.Folder(@"C:\B", "000000000000000000000000000000D1"), Ct);
        found!.Id.ShouldBe(a.Id);
        found.Folder.PathDisplay.ShouldBe(@"C:\A");
    }

    // ── events ──

    [Fact]
    public async Task NoteChanged_IsRaisedForEachWrite_AndAThrowingSubscriberIsIsolated()
    {
        var kinds = new List<NoteChangeKind>();
        Store.NoteChanged += (_, _) => throw new InvalidOperationException("bad subscriber");
        Store.NoteChanged += (_, e) => kinds.Add(e.Kind);
        var labelsChanged = 0;
        Store.LabelsChanged += (_, _) => labelsChanged++;

        var n = await Store.UpsertAsync(_f.NewNote(@"C:\A") with { Labels = [Label.Create("fresh", "red")] }, Ct);
        await Store.UpsertAsync(n with { Title = "t2" }, Ct);
        await Store.DeleteAsync(n.Id, Ct);
        await Store.RestoreAsync(n.Id, Ct);

        kinds.ShouldBe([NoteChangeKind.Created, NoteChangeKind.Updated, NoteChangeKind.Deleted, NoteChangeKind.Restored]);
        labelsChanged.ShouldBe(1);
    }

    [Fact]
    public async Task FailedWrite_RaisesNoEvent_AndTheWriterKeepsWorking()
    {
        var events = 0;
        Store.NoteChanged += (_, _) => events++;
        await Store.UpsertAsync(_f.NewNote(@"C:\A"), Ct);
        await Should.ThrowAsync<DuplicateFolderNoteException>(() => Store.UpsertAsync(_f.NewNote(@"C:\A"), Ct));
        await Store.UpsertAsync(_f.NewNote(@"C:\B"), Ct);
        events.ShouldBe(2);
    }

    [Fact]
    public async Task Disposed_StoreRejectsCalls()
    {
        await _f.Store.DisposeAsync();
        await Should.ThrowAsync<ObjectDisposedException>(() => _f.Store.UpsertAsync(_f.NewNote(@"C:\A"), Ct));
        await Should.ThrowAsync<ObjectDisposedException>(() => _f.Store.ListAsync(NoteQuery.All, Ct));
        await _f.ReopenAsync();
    }
}
