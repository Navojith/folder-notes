using FolderNotes.Core.Domain;
using FolderNotes.Core.Identity;
using FolderNotes.Core.Storage;

namespace FolderNotes.Core.Tests.Storage;

/// <summary>REQ-060/061 end to end: real temp folders + real identity + real store.</summary>
public sealed class OrphanScannerTests : IAsyncLifetime
{
    private readonly FolderIdentityService _identity = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fn-orphan-" + Guid.NewGuid().ToString("N")[..8]);
    private StoreFixture _f = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _f = await StoreFixture.CreateAsync(_identity);
    }

    public async ValueTask DisposeAsync()
    {
        await _f.DisposeAsync();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private async Task<Note> NoteFor(string name)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
        return await _f.Store.UpsertAsync(Note.CreateFor(_identity.Resolve(dir), _f.Clock), Ct);
    }

    [Fact]
    public async Task REQ060_REQ061_Scan_RelinksMoved_OrphansDeleted_LeavesOthers()
    {
        var moved = await NoteFor("Moved");
        var deleted = await NoteFor("Deleted");
        var untouched = await NoteFor("Untouched");
        Directory.Move(Path.Combine(_root, "Moved"), Path.Combine(_root, "Moved-renamed"));
        Directory.Delete(Path.Combine(_root, "Deleted"));

        var report = await new OrphanScanner(_f.Store, _identity).ScanAsync(Ct);

        report.ShouldBe(new OrphanScanReport(Checked: 3, Relinked: 1, Orphaned: 1, Recovered: 0));
        (await _f.Store.GetByIdAsync(moved.Id, Ct))!.Folder.PathDisplay.ShouldEndWith(@"\Moved-renamed");
        (await _f.Store.GetByIdAsync(deleted.Id, Ct))!.IsOrphaned.ShouldBeTrue();
        (await _f.Store.GetByIdAsync(untouched.Id, Ct)).ShouldBeEquivalentTo(untouched);

        // A second scan has nothing left to do.
        (await new OrphanScanner(_f.Store, _identity).ScanAsync(Ct)).ShouldBe(new OrphanScanReport(3, 0, 0, 0));
    }

    [Fact]
    public async Task REQ060_Scan_FolderRecreatedAtSamePath_KeepsTheNote()
    {
        var bin = await NoteFor("bin");
        var path = Path.Combine(_root, "bin");
        Directory.Delete(path);
        Directory.CreateDirectory(path);

        var report = await new OrphanScanner(_f.Store, _identity).ScanAsync(Ct);

        report.Relinked.ShouldBe(1);
        var now = await _f.Store.GetByIdAsync(bin.Id, Ct);
        now!.Folder.FileId.ShouldBe(_identity.Resolve(path).FileId);
        now.IsOrphaned.ShouldBeFalse();
    }

    [Fact]
    public async Task REQ061_Scan_OrphanThatReappears_IsRecovered()
    {
        var n = await NoteFor("Comes back");
        await _f.Store.SetOrphanedAsync(n.Id, true, Ct);

        var report = await new OrphanScanner(_f.Store, _identity).ScanAsync(Ct);

        report.Recovered.ShouldBe(1);
        (await _f.Store.GetByIdAsync(n.Id, Ct))!.IsOrphaned.ShouldBeFalse();
    }

    [Fact]
    public async Task REQ060_GetByFolder_RealRenameAndRecreate()
    {
        var acme = await NoteFor("Acme");
        Directory.Move(Path.Combine(_root, "Acme"), Path.Combine(_root, "Acme Ltd"));

        // Rename: found by file ID at the new path.
        (await _f.Store.GetByFolderAsync(_identity.Resolve(Path.Combine(_root, "Acme Ltd")), Ct))!.Id.ShouldBe(acme.Id);

        // A new folder created where Acme used to be is a *different* folder (the original is alive).
        var newcomer = Directory.CreateDirectory(Path.Combine(_root, "Acme")).FullName;
        (await _f.Store.GetByFolderAsync(_identity.Resolve(newcomer), Ct)).ShouldBeNull();
    }
}
