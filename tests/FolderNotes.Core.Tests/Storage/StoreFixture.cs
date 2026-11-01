using System.Diagnostics.CodeAnalysis;
using FolderNotes.Core.Domain;
using FolderNotes.Core.Identity;
using FolderNotes.Core.Paths;
using FolderNotes.Core.Storage;
using Microsoft.Extensions.Time.Testing;

namespace FolderNotes.Core.Tests.Storage;

/// <summary>A store on a temp-file database (WAL needs a real file), with a fake clock and identity.</summary>
public sealed class StoreFixture : IAsyncDisposable
{
    private StoreFixture(string dir, SqliteNoteStore store, FakeTimeProvider clock, FakeIdentity identity)
    {
        Directory = dir;
        Store = store;
        Clock = clock;
        Identity = identity;
    }

    public string Directory { get; }
    public string DbPath => Path.Combine(Directory, "notes.db");
    public SqliteNoteStore Store { get; private set; }
    public FakeTimeProvider Clock { get; }
    public FakeIdentity Identity { get; }

    public static async Task<StoreFixture> CreateAsync(IFolderIdentityService? identity = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "fn-db-" + Guid.NewGuid().ToString("N")[..8]);
        System.IO.Directory.CreateDirectory(dir);
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 31, 9, 0, 0, TimeSpan.Zero));
        var fake = new FakeIdentity();
        var store = await SqliteNoteStore.OpenAsync(new SqliteNoteStoreOptions
        {
            DatabasePath = Path.Combine(dir, "notes.db"),
            Identity = identity ?? fake,
            Clock = clock,
        });
        return new StoreFixture(dir, store, clock, fake);
    }

    public async Task ReopenAsync()
    {
        await Store.DisposeAsync();
        Store = await SqliteNoteStore.OpenAsync(new SqliteNoteStoreOptions { DatabasePath = DbPath, Identity = Identity, Clock = Clock });
    }

    public async ValueTask DisposeAsync()
    {
        await Store.DisposeAsync();
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    public static FolderRef Folder(string path, string? fileId = null, string serial = "00000000CAFEBABE") =>
        new(PathNormalizer.Normalize(path), PathNormalizer.NormalizeDisplay(path), fileId is null ? null : serial, fileId);

    public Note NewNote(string path, string title = "Note", string? fileId = null) =>
        Note.CreateFor(Folder(path, fileId), Clock) with { Title = title };
}

/// <summary>
/// In-memory identity: tests decide where each file ID "is" (<see cref="Place"/>) and which folder
/// sits at a path (<see cref="PutFolder"/>), including folders on other volumes (ADR-020).
/// </summary>
public sealed class FakeIdentity : IFolderIdentityService
{
    private readonly Dictionary<string, LocateResult> _locations = [];
    private readonly Dictionary<string, FolderRef> _folders = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _dirs = new(StringComparer.OrdinalIgnoreCase);

    public void Place(string fileId, LocateStatus status, string? path) => _locations[fileId] = new LocateResult(status, path);

    /// <summary>A folder exists at <paramref name="folder"/>'s path (and its drive root exists).</summary>
    public void PutFolder(FolderRef folder)
    {
        _folders[folder.PathDisplay] = folder;
        _dirs.Add(folder.PathDisplay);
        if (Path.GetPathRoot(folder.PathDisplay) is { Length: > 0 } root)
        {
            _dirs.Add(Trim(root));
        }
    }

    public void RemoveFolder(string path)
    {
        _folders.Remove(Trim(path));
        _dirs.Remove(Trim(path));
    }

    /// <summary>Only the drive root exists (the drive is "mounted").</summary>
    public void PutRoot(string root) => _dirs.Add(Trim(root));

    /// <summary>Directory-exists for <see cref="OrphanScanner"/> and <see cref="RelinkTarget"/>.</summary>
    public bool Exists(string path) => _dirs.Contains(Trim(path));

    public FolderRef Resolve(string path) => TryResolve(path, out var f) ? f : throw new ArgumentException(path, nameof(path));

    public bool TryResolve(string path, [NotNullWhen(true)] out FolderRef? folder)
    {
        if (!PathNormalizer.TryNormalize(path, out _))
        {
            folder = null;
            return false;
        }

        folder = _folders.TryGetValue(PathNormalizer.NormalizeDisplay(path), out var placed) ? placed : StoreFixture.Folder(path);
        return true;
    }

    public LocateResult Locate(FolderRef storedRef)
    {
        ArgumentNullException.ThrowIfNull(storedRef);
        if (storedRef.FileId is { } id && _locations.TryGetValue(id, out var r))
        {
            return r;
        }

        return !storedRef.HasFileId && Exists(storedRef.PathDisplay) ? new LocateResult(LocateStatus.Found, storedRef.PathDisplay) : LocateResult.NotFound;
    }

    private static string Trim(string p) => p.TrimEnd('\\');
}
