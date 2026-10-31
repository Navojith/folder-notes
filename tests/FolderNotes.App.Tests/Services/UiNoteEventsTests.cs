using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using FolderNotes.App.Services;
using FolderNotes.Core.Domain;
using FolderNotes.Core.Identity;
using FolderNotes.Core.Paths;
using FolderNotes.Core.Storage;
using Serilog.Core;

namespace FolderNotes.App.Tests.Services;

/// <summary>O-11 / ADR-011: store events reach UI subscribers only through the UI dispatcher.</summary>
public sealed class UiNoteEventsTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fn-app-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly FakeDispatcher _ui = new();
    private SqliteNoteStore _store = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _store = await SqliteNoteStore.OpenAsync(new SqliteNoteStoreOptions
        {
            DatabasePath = Path.Combine(_dir, "notes.db"),
            Identity = new PathOnlyIdentity(),
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

    [Fact]
    public async Task O11_NoteChanged_IsRaisedOnlyWhenTheUiQueueRuns()
    {
        using var relay = new UiNoteEvents(_store, _store, _ui, Logger.None);
        var seen = new List<(NoteChangeKind Kind, int Thread)>();
        relay.NoteChanged += (_, e) => seen.Add((e.Kind, Environment.CurrentManagedThreadId));

        await _store.UpsertAsync(Note.CreateFor(Folder(@"C:\Clients\Acme")) with { Title = "Acme" }, Ct);

        seen.ShouldBeEmpty(); // not raised inline on the writer task
        _ui.Pending.ShouldBe(1);

        var uiThread = Environment.CurrentManagedThreadId;
        _ui.Pump();
        seen.ShouldBe([(NoteChangeKind.Created, uiThread)]);
    }

    [Fact]
    public async Task O11_LabelsChanged_IsMarshalledToo()
    {
        using var relay = new UiNoteEvents(_store, _store, _ui, Logger.None);
        var raised = 0;
        relay.LabelsChanged += (_, _) => raised++;

        await _store.CreateLabelAsync("o11-relay-test", "blue", Ct);

        raised.ShouldBe(0);
        _ui.Pump();
        raised.ShouldBe(1);
    }

    [Fact]
    public async Task O11_ThrowingUiHandler_DoesNotBreakLaterNotifications()
    {
        using var relay = new UiNoteEvents(_store, _store, _ui, Logger.None);
        var calls = 0;
        relay.NoteChanged += (_, _) =>
        {
            calls++;
            throw new InvalidOperationException("handler bug");
        };

        await _store.UpsertAsync(Note.CreateFor(Folder(@"C:\A")), Ct);
        await _store.UpsertAsync(Note.CreateFor(Folder(@"C:\B")), Ct);

        Should.NotThrow(_ui.Pump);
        calls.ShouldBe(2);
    }

    [Fact]
    public async Task O11_ClosedUiQueue_DropsSilently()
    {
        using var relay = new UiNoteEvents(_store, _store, _ui, Logger.None);
        relay.NoteChanged += (_, _) => throw new InvalidOperationException("must not run");
        _ui.Closed = true;

        await _store.UpsertAsync(Note.CreateFor(Folder(@"C:\A")), Ct);

        _ui.Pending.ShouldBe(0);
    }

    [Fact]
    public async Task O11_Dispose_Unsubscribes()
    {
        var relay = new UiNoteEvents(_store, _store, _ui, Logger.None);
        relay.Dispose();

        await _store.UpsertAsync(Note.CreateFor(Folder(@"C:\A")), Ct);

        _ui.Pending.ShouldBe(0);
    }

    private static FolderRef Folder(string path) =>
        new(PathNormalizer.Normalize(path), PathNormalizer.NormalizeDisplay(path), null, null);

    private sealed class FakeDispatcher : IUiDispatcher
    {
        private readonly ConcurrentQueue<Action> _queue = new();

        public bool Closed { get; set; }

        public int Pending => _queue.Count;

        public bool Post(Action action)
        {
            if (Closed)
            {
                return false;
            }

            _queue.Enqueue(action);
            return true;
        }

        public void Pump()
        {
            while (_queue.TryDequeue(out var action))
            {
                action();
            }
        }
    }

    private sealed class PathOnlyIdentity : IFolderIdentityService
    {
        public FolderRef Resolve(string path) => Folder(path);

        public bool TryResolve(string path, [NotNullWhen(true)] out FolderRef? folder)
        {
            folder = Folder(path);
            return true;
        }

        public LocateResult Locate(FolderRef storedRef) => LocateResult.NotFound;
    }
}
