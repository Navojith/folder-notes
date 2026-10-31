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

/// <summary>In-memory identity: tests decide where each file ID "is".</summary>
public sealed class FakeIdentity : IFolderIdentityService
{
    private readonly Dictionary<string, LocateResult> _locations = [];

    public void Place(string fileId, LocateStatus status, string? path) => _locations[fileId] = new LocateResult(status, path);

    public FolderRef Resolve(string path) => StoreFixture.Folder(path);

    public bool TryResolve(string path, [NotNullWhen(true)] out FolderRef? folder)
    {
        folder = PathNormalizer.TryNormalize(path, out _) ? StoreFixture.Folder(path) : null;
        return folder is not null;
    }

    public LocateResult Locate(FolderRef storedRef) =>
        storedRef.FileId is { } id && _locations.TryGetValue(id, out var r) ? r : LocateResult.NotFound;
}
