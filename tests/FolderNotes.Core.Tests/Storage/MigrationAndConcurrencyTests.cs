using System.Collections.Concurrent;
using FolderNotes.Core.Domain;
using FolderNotes.Core.Storage;
using Microsoft.Data.Sqlite;

namespace FolderNotes.Core.Tests.Storage;

public sealed class MigrationAndConcurrencyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Migration_BacksUpTheDatabaseBeforeMigrating()
    {
        await using var f = await StoreFixture.CreateAsync();
        var note = await f.Store.UpsertAsync(f.NewNote(@"C:\Keep me", "v1 note"), Ct);
        await f.Store.DisposeAsync();

        var v2 = Schema.Migrations.Append(new Migration(2, (c, tx) =>
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "ALTER TABLE notes ADD COLUMN pinned INTEGER NOT NULL DEFAULT 0";
            cmd.ExecuteNonQuery();
        })).ToList();
        await using (var migrated = await SqliteNoteStore.OpenAsync(new SqliteNoteStoreOptions
        {
            DatabasePath = f.DbPath,
            Identity = f.Identity,
            Clock = f.Clock,
            Migrations = v2,
        }, Ct))
        {
            (await migrated.GetByIdAsync(note.Id, Ct))!.Title.ShouldBe("v1 note");
        }

        var backup = Directory.GetFiles(Path.Combine(f.Directory, "backups")).ShouldHaveSingleItem();
        Path.GetFileName(backup).ShouldBe("notes-v1-20261031090000.db");
        await using var c = new SqliteConnection($"Data Source={backup};Mode=ReadOnly;Pooling=False");
        await c.OpenAsync(Ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT title FROM notes";
        ((string)cmd.ExecuteScalar()!).ShouldBe("v1 note");
        cmd.CommandText = "PRAGMA user_version";
        Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture).ShouldBe(1);

        // This app build only knows v1, so the v2 file is refused rather than downgraded.
        await Should.ThrowAsync<UnsupportedSchemaException>(f.ReopenAsync);
    }

    [Fact]
    public async Task Migration_NewerSchema_IsRefused()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fn-db-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var db = Path.Combine(dir, "notes.db");
        try
        {
            await using (var c = new SqliteConnection($"Data Source={db};Pooling=False"))
            {
                await c.OpenAsync(Ct);
                using var cmd = c.CreateCommand();
                cmd.CommandText = "PRAGMA user_version = 99";
                cmd.ExecuteNonQuery();
            }

            var ex = await Should.ThrowAsync<UnsupportedSchemaException>(() =>
                SqliteNoteStore.OpenAsync(new SqliteNoteStoreOptions { DatabasePath = db, Identity = new FakeIdentity() }, Ct));
            (ex.Found, ex.Supported).ShouldBe((99, Schema.CurrentVersion));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Concurrency_ReadersRunAlongsideWriters_WithoutErrors()
    {
        await using var f = await StoreFixture.CreateAsync();
        const int writers = 3;
        const int perWriter = 100;
        var errors = new ConcurrentQueue<Exception>();
        using var stop = new CancellationTokenSource();

        var readers = Enumerable.Range(0, 4).Select(r => Task.Run(async () =>
        {
            var last = 0;
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    var count = (await f.Store.ListAsync(NoteQuery.All, Ct)).Count;
                    if (count < last)
                    {
                        errors.Enqueue(new InvalidOperationException($"reader {r} saw count go back from {last} to {count}"));
                    }

                    last = count;
                    _ = await f.Store.GetByFolderAsync(StoreFixture.Folder(@"C:\W0\N0"), Ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    errors.Enqueue(ex);
                }
            }
        }, Ct)).ToArray();

        var writerTasks = Enumerable.Range(0, writers).Select(w => Task.Run(async () =>
        {
            for (var i = 0; i < perWriter; i++)
            {
                var n = await f.Store.UpsertAsync(f.NewNote($@"C:\W{w}\N{i}", $"w{w} n{i}") with { Labels = [Label.Create($"w{w}", "blue")] }, Ct);
                if (i % 10 == 0)
                {
                    await f.Store.UpsertAsync(n with { BodyMd = "edited" }, Ct);
                }
            }
        }, Ct)).ToArray();

        await Task.WhenAll(writerTasks);
        await stop.CancelAsync();
        await Task.WhenAll(readers);

        errors.ShouldBeEmpty();
        (await f.Store.ListAsync(NoteQuery.All, Ct)).Count.ShouldBe(writers * perWriter);
        (await f.Store.ListAsync(new NoteQuery { Labels = ["w1"] }, Ct)).Count.ShouldBe(perWriter);
    }

    [Fact]
    public async Task Concurrency_AnExternalReadOnlyReader_SeesCommittedData()
    {
        // The shell extension's view (02 § Process & concurrency): a separate read-only connection.
        await using var f = await StoreFixture.CreateAsync();
        await using var reader = new SqliteConnection($"Data Source={f.DbPath};Mode=ReadOnly;Pooling=False;Default Timeout=1");
        await reader.OpenAsync(Ct);

        var note = await f.Store.UpsertAsync(f.NewNote(@"C:\Clients\Acme", "Tooltip me") with { Labels = [Label.Create("client", "blue")] }, Ct);

        using var cmd = reader.CreateCommand();
        cmd.CommandText = """
            SELECT n.title, (SELECT group_concat(l.name, ', ') FROM note_labels nl JOIN labels l ON l.id = nl.label_id WHERE nl.note_id = n.id)
            FROM notes n WHERE n.deleted_at IS NULL AND (n.path_norm = $p OR (n.volume_serial = $v AND n.file_id = $f)) LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$p", note.Folder.PathNorm);
        cmd.Parameters.AddWithValue("$v", DBNull.Value);
        cmd.Parameters.AddWithValue("$f", DBNull.Value);
        using var r = cmd.ExecuteReader();
        r.Read().ShouldBeTrue();
        (r.GetString(0), r.GetString(1)).ShouldBe(("Tooltip me", "client"));
    }

    [Fact]
    public async Task Cancellation_BeforeTheWriteRuns_CancelsIt()
    {
        await using var f = await StoreFixture.CreateAsync();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Should.ThrowAsync<TaskCanceledException>(() => f.Store.UpsertAsync(f.NewNote(@"C:\A"), cts.Token));
        (await f.Store.ListAsync(NoteQuery.All, Ct)).ShouldBeEmpty();
    }
}
