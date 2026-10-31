using System.Text;
using FolderNotes.Core.Domain;
using FolderNotes.Core.Portability;
using FolderNotes.Core.Storage;
using FolderNotes.Core.Tests.Storage;

namespace FolderNotes.Core.Tests.Portability;

public sealed class NotePortabilityTests : IAsyncLifetime
{
    private StoreFixture _src = null!;
    private StoreFixture _dst = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _src = await StoreFixture.CreateAsync();
        _dst = await StoreFixture.CreateAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _src.DisposeAsync();
        await _dst.DisposeAsync();
    }

    private static NotePortability For(StoreFixture f) => new(f.Store, f.Identity);

    private async Task<List<Note>> SeedVariedAsync()
    {
        var client = (await _src.Store.ListLabelsAsync(Ct)).Single(l => l.Name == "client");
        await _src.Store.UpdateLabelAsync(client with { Color = "red" }, Ct); // recolored starter label must survive
        var notes = new List<Note>
        {
            await _src.Store.UpsertAsync(_src.NewNote(@"D:\Clients\Acme Ltd", "Client: Acme Ltd") with
            {
                BodyMd = "# Retention\n\n- [x] invoices\n- [ ] contracts\n\n\"quoted\" \\ backslash\tTab",
                Labels = [Label.Create("client", "blue"), Label.Create("Ünïcode ✓", "teal")],
                Status = NoteStatus.Keep,
                AccentColor = "indigo",
                KeepUntil = new DateOnly(2027, 1, 1),
                ReviewOn = new DateOnly(2026, 12, 1),
                SafeAfter = new DateOnly(2028, 2, 29),
                TravelWithFolder = true,
            }, Ct),
            await _src.Store.UpsertAsync(_src.NewNote(@"\\nas\media\Photos 📷", "Photos 📷") with { Status = NoteStatus.Archive }, Ct),
            await _src.Store.UpsertAsync(_src.NewNote(@"C:\Dev\app\bin", "bin") with { Status = NoteStatus.SafeToDelete, BodyMd = new string('x', 50_000) }, Ct),
        };
        _src.Clock.Advance(TimeSpan.FromHours(3));
        notes[1] = await _src.Store.UpsertAsync(notes[1] with { BodyMd = "edited later" }, Ct); // created_at != updated_at
        return notes;
    }

    private static async Task<ExportDocument> RoundTripThroughJson(ExportDocument doc)
    {
        using var ms = new MemoryStream();
        await NotePortability.WriteAsync(ms, doc, Ct);
        ms.Position = 0;
        return await NotePortability.ReadAsync(ms, Ct);
    }

    [Fact]
    public async Task REQ070_ExportImport_RoundTripIsLossless()
    {
        var original = await SeedVariedAsync();
        var doc = await RoundTripThroughJson(await For(_src).ExportAsync(ct: Ct));

        var report = await For(_dst).ImportAsync(doc, ct: Ct);

        (report.Created, report.Updated, report.Unchanged).ShouldBe((3, 0, 0));
        report.Skipped.ShouldBeEmpty();
        foreach (var o in original)
        {
            var copy = await _dst.Store.GetByIdAsync(o.Id, Ct);
            copy.ShouldNotBeNull();
            (copy with { Labels = [] }).ShouldBe(o with { Labels = [] });
            copy.Labels.Select(l => (l.Name, l.Color)).ShouldBe(o.Labels.Select(l => (l.Name, l.Color)));
        }

        var srcLabels = (await _src.Store.ListLabelsAsync(Ct)).Select(l => (l.Name, l.Color));
        var dstLabels = (await _dst.Store.ListLabelsAsync(Ct)).Select(l => (l.Name, l.Color));
        dstLabels.ShouldBe(srcLabels);
    }

    [Fact]
    public async Task REQ070_ExportOfImport_IsByteIdentical()
    {
        await SeedVariedAsync();
        _dst.Clock.SetUtcNow(_src.Clock.GetUtcNow());
        var first = await For(_src).ExportAsync(ct: Ct);
        await For(_dst).ImportAsync(first, ct: Ct);
        var second = await For(_dst).ExportAsync(ct: Ct);

        (await ToJson(second)).ShouldBe(await ToJson(first));
    }

    [Fact]
    public async Task REQ070_FileFormat_IsSnakeCaseWithSchema()
    {
        await SeedVariedAsync();
        var json = await ToJson(await For(_src).ExportAsync(ct: Ct));

        json.ShouldContain("\"schema\": \"foldernotes-export/1\"");
        json.ShouldContain("\"path_display\": \"D:\\\\Clients\\\\Acme Ltd\"");
        json.ShouldContain("\"keep_until\": \"2027-01-01\"");
        json.ShouldContain("\"review_on\": null");
        json.ShouldContain("Ünïcode ✓"); // readable, not \u-escaped
        json.ShouldNotContain("file_id");
        NotePortability.DefaultFileName(new DateOnly(2026, 10, 31)).ShouldBe("foldernotes-export-20261031.json");
    }

    [Fact]
    public async Task REQ070_SelectionExport_HasOnlyThoseNotesAndTheirLabels()
    {
        var notes = await SeedVariedAsync();
        var doc = await For(_src).ExportAsync([notes[0].Id], Ct);

        doc.Notes.Single().Id.ShouldBe(notes[0].Id);
        doc.Labels.Select(l => l.Name).ShouldBe(["client", "Ünïcode ✓"]);
    }

    [Fact]
    public async Task REQ070_DeletedNotes_AreNotExported()
    {
        var notes = await SeedVariedAsync();
        await _src.Store.DeleteAsync(notes[2].Id, Ct);
        (await For(_src).ExportAsync(ct: Ct)).Notes.Select(n => n.Id).ShouldNotContain(notes[2].Id);
    }

    [Theory]
    [InlineData(ImportConflictPolicy.NewerWins, "newer import", 1, 0)]
    [InlineData(ImportConflictPolicy.Overwrite, "newer import", 1, 0)]
    [InlineData(ImportConflictPolicy.KeepExisting, "local", 0, 1)]
    public async Task REQ070_MatchByPath_PolicyDecides(ImportConflictPolicy policy, string expectedTitle, int updated, int unchanged)
    {
        var local = await _dst.Store.UpsertAsync(_dst.NewNote(@"D:\Shared", "local"), Ct);
        var incoming = new ExportDocument
        {
            Notes = [Entry(@"d:\shared\", "newer import", updatedAt: local.UpdatedAt.AddDays(1))],
        };

        var report = await For(_dst).ImportAsync(incoming, policy, Ct);

        (report.Updated, report.Unchanged, report.Created).ShouldBe((updated, unchanged, 0));
        var now = (await _dst.Store.ListAsync(NoteQuery.All, Ct)).Single();
        now.Title.ShouldBe(expectedTitle);
        now.Id.ShouldBe(local.Id); // keeps the local id
        now.CreatedAt.ShouldBe(local.CreatedAt);
    }

    [Fact]
    public async Task REQ070_NewerWins_OlderImportIsIgnored_OverwriteReplaces()
    {
        var local = await _dst.Store.UpsertAsync(_dst.NewNote(@"D:\Shared", "local"), Ct);
        var older = new ExportDocument { Notes = [Entry(@"D:\Shared", "old import", updatedAt: local.UpdatedAt.AddDays(-1))] };

        (await For(_dst).ImportAsync(older, ct: Ct)).Unchanged.ShouldBe(1);
        (await _dst.Store.GetByIdAsync(local.Id, Ct))!.Title.ShouldBe("local");

        (await For(_dst).ImportAsync(older, ImportConflictPolicy.Overwrite, Ct)).Updated.ShouldBe(1);
        (await _dst.Store.GetByIdAsync(local.Id, Ct))!.Title.ShouldBe("old import");
    }

    [Fact]
    public async Task REQ070_ReimportingTheSameFile_ChangesNothing()
    {
        await SeedVariedAsync();
        var doc = await For(_src).ExportAsync(ct: Ct);
        await For(_dst).ImportAsync(doc, ct: Ct);

        var again = await For(_dst).ImportAsync(doc, ct: Ct);
        (again.Created, again.Updated, again.Unchanged).ShouldBe((0, 0, 3));
        again.Skipped.ShouldBeEmpty();
    }

    [Fact]
    public async Task REQ070_NoPathMatch_SameId_MatchesById()
    {
        var local = await _dst.Store.UpsertAsync(_dst.NewNote(@"D:\Old place", "local"), Ct);
        var doc = new ExportDocument { Notes = [Entry(@"E:\New place", "moved", id: local.Id, updatedAt: local.UpdatedAt.AddMinutes(1))] };

        (await For(_dst).ImportAsync(doc, ct: Ct)).Updated.ShouldBe(1);
        var now = await _dst.Store.GetByIdAsync(local.Id, Ct);
        (now!.Title, now.Folder.PathDisplay).ShouldBe(("moved", @"E:\New place"));
    }

    [Fact]
    public async Task REQ070_IdUsedByDeletedNote_GetsANewId()
    {
        var gone = await _dst.Store.UpsertAsync(_dst.NewNote(@"D:\Gone", "gone"), Ct);
        await _dst.Store.DeleteAsync(gone.Id, Ct);
        var doc = new ExportDocument { Notes = [Entry(@"D:\Elsewhere", "fresh", id: gone.Id)] };

        (await For(_dst).ImportAsync(doc, ct: Ct)).Created.ShouldBe(1);
        var created = (await _dst.Store.ListAsync(NoteQuery.All, Ct)).Single();
        created.Id.ShouldNotBe(gone.Id);
        (await _dst.Store.GetByIdAsync(gone.Id, Ct))!.IsDeleted.ShouldBeTrue(); // still restorable
    }

    [Fact]
    public async Task REQ070_InvalidEntries_AreSkippedAndReported_OthersImported()
    {
        var doc = new ExportDocument
        {
            Labels = [new ExportLabel("bad\"name", "blue"), new ExportLabel("ok-label", "lime")],
            Notes =
            [
                Entry(@"D:\Good", "good", labels: ["ok-label", "unlisted"]),
                Entry(@"relative\path", "bad path"),
                Entry(@"D:\TooLong", new string('t', 121)),
                Entry(@"D:\BadStatus", "x") with { Status = "deleted" },
                Entry(@"D:\BadTime", "x") with { UpdatedAt = "yesterday" },
                Entry(@"D:\Good", "same path twice, older", updatedAt: DateTimeOffset.UnixEpoch),
            ],
        };

        var report = await For(_dst).ImportAsync(doc, ct: Ct);

        report.Created.ShouldBe(1);
        report.Unchanged.ShouldBe(1);
        report.Skipped.Select(s => s.Index).ShouldBe([1, 2, 3, 4]);
        var good = (await _dst.Store.ListAsync(NoteQuery.All, Ct)).Single();
        good.Labels.Select(l => (l.Name, l.Color)).ShouldBe([("ok-label", "lime"), ("unlisted", "blue")]);
        (await _dst.Store.ListLabelsAsync(Ct)).ShouldNotContain(l => l.Name.Contains('"', StringComparison.Ordinal));
    }

    [Fact]
    public async Task REQ070_DuplicateFileIdInImport_IsSkippedNotFatal()
    {
        // Two entries resolving to the same folder ID would violate REQ-001. The second is skipped, and the batch survives.
        var identity = new SameIdIdentity();
        var doc = new ExportDocument { Notes = [Entry(@"D:\One", "one"), Entry(@"D:\Two", "two")] };

        var report = await new NotePortability(_dst.Store, identity).ImportAsync(doc, ct: Ct);

        report.Created.ShouldBe(1);
        report.Skipped.Single().Index.ShouldBe(1);
        (await _dst.Store.ListAsync(NoteQuery.All, Ct)).Single().Title.ShouldBe("one");
    }

    [Theory]
    [InlineData("{ \"schema\": \"something-else/9\", \"notes\": [] }")]
    [InlineData("{ not json")]
    [InlineData("null")]
    public async Task REQ070_BadFile_IsRefused(string content)
    {
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(content));
        await Should.ThrowAsync<ExportFormatException>(() => NotePortability.ReadAsync(ms, Ct));
    }

    [Fact]
    public async Task REQ070_ImportRaisesEvents_ForImportedNotesOnly()
    {
        var events = new List<NoteChangedEventArgs>();
        _dst.Store.NoteChanged += (_, e) => events.Add(e);
        var doc = new ExportDocument { Notes = [Entry(@"D:\A", "a"), Entry(@"D:\B", new string('t', 121))] };

        await For(_dst).ImportAsync(doc, ct: Ct);

        events.ShouldHaveSingleItem().Kind.ShouldBe(NoteChangeKind.Created);
    }

    private static ExportNote Entry(string path, string title, Guid? id = null, DateTimeOffset? updatedAt = null, string[]? labels = null)
    {
        var t = updatedAt ?? new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var created = t < DateTimeOffset.UnixEpoch.AddDays(1) ? t : t.AddDays(-1);
        return new ExportNote
        {
            Id = id ?? Guid.NewGuid(),
            PathDisplay = path,
            Title = title,
            Labels = labels ?? [],
            CreatedAt = StorageFormat.Timestamp(created),
            UpdatedAt = StorageFormat.Timestamp(t),
        };
    }

    private static async Task<string> ToJson(ExportDocument doc)
    {
        using var ms = new MemoryStream();
        await NotePortability.WriteAsync(ms, doc, Ct);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>Every path resolves to the same file ID.</summary>
    private sealed class SameIdIdentity : FolderNotes.Core.Identity.IFolderIdentityService
    {
        public FolderRef Resolve(string path) => StoreFixture.Folder(path, "000000000000000000000000000000EE");

        public bool TryResolve(string path, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out FolderRef? folder)
        {
            folder = Resolve(path);
            return true;
        }

        public FolderNotes.Core.Identity.LocateResult Locate(FolderRef storedRef) => FolderNotes.Core.Identity.LocateResult.NotFound;
    }
}
