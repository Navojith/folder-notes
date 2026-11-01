using System.Text.Json;
using System.Threading.Channels;
using FolderNotes.Core.Domain;
using FolderNotes.Core.Identity;
using FolderNotes.Core.Paths;
using Microsoft.Data.Sqlite;

namespace FolderNotes.Core.Storage;

public sealed record SqliteNoteStoreOptions
{
    public required string DatabasePath { get; init; }

    /// <summary>Where migration backups go. Default: <c>backups</c> next to the database.</summary>
    public string? BackupDirectory { get; init; }

    public required IFolderIdentityService Identity { get; init; }
    public TimeProvider Clock { get; init; } = TimeProvider.System;

    /// <summary>Tests only: replaces the built-in migrations.</summary>
    internal IReadOnlyList<Migration>? Migrations { get; init; }
}

/// <summary>
/// The SQLite store (specs/03-data-model.md, ADR-003, ADR-010, ADR-011): a single serialized writer
/// plus pooled read-only readers over a WAL database.
/// </summary>
public sealed partial class SqliteNoteStore : INoteRepository, ILabelRepository, IAsyncDisposable
{
    private const string NoteColumns =
        "n.rowid, n.id, n.path_norm, n.path_display, n.volume_serial, n.file_id, n.volume_kind, n.title, n.body_md, " +
        "n.status, n.accent_color, n.keep_until, n.review_on, n.safe_after, n.travel_with_folder, " +
        "n.created_at, n.updated_at, n.deleted_at, n.orphaned_at";

    internal const string DueExpression = "coalesce(min(n.review_on, n.safe_after), n.review_on, n.safe_after)";

    private readonly SqliteNoteStoreOptions _options;
    private readonly string _readConnectionString;
    private readonly SqliteConnection _writer;
    private readonly Channel<WriteOp> _queue = Channel.CreateUnbounded<WriteOp>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _loop;
    private int _disposed;

    private SqliteNoteStore(SqliteNoteStoreOptions options, SqliteConnection writer, string readConnectionString)
    {
        _options = options;
        _writer = writer;
        _readConnectionString = readConnectionString;
        _loop = Task.Run(WriterLoopAsync);
    }

    public event EventHandler<NoteChangedEventArgs>? NoteChanged;

    public event EventHandler? LabelsChanged;

    public string DatabasePath => _options.DatabasePath;

    internal TimeProvider Clock => _options.Clock;

    /// <summary>Opens (creating and migrating if needed) the database.</summary>
    public static async Task<SqliteNoteStore> OpenAsync(SqliteNoteStoreOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var full = Path.GetFullPath(options.DatabasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        options = options with { DatabasePath = full };

        var writeCs = new SqliteConnectionStringBuilder
        {
            DataSource = full,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            Pooling = false,
        }.ToString();
        var readCs = new SqliteConnectionStringBuilder
        {
            DataSource = full,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = true,
            DefaultTimeout = 5,
        }.ToString();

        var writer = new SqliteConnection(writeCs);
        try
        {
            await writer.OpenAsync(ct).ConfigureAwait(false);
            Exec(writer, null, "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;");
            Migrate(writer, options);
        }
        catch
        {
            await writer.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return new SqliteNoteStore(options, writer, readCs);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _queue.Writer.TryComplete();
        await _loop.ConfigureAwait(false);
        await _writer.DisposeAsync().ConfigureAwait(false);
        using (var c = new SqliteConnection(_readConnectionString))
        {
            SqliteConnection.ClearPool(c);
        }
    }

    // ───────────────────────────── notes: reads ─────────────────────────────

    public Task<Note?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        ReadAsync(c => LoadNotes(c, null, "n.id = $id", cmd => cmd.Parameters.AddWithValue("$id", StorageFormat.Id(id))).FirstOrDefault(), ct);

    public Task<IReadOnlyList<Note>> ListAsync(NoteQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return ReadAsync<IReadOnlyList<Note>>(c => QueryNotes(c, query), ct);
    }

    public Task<Note?> GetByFolderAsync(FolderRef folder, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        return GetByFolderCoreAsync(folder, retry: true, ct);
    }

    private async Task<Note?> GetByFolderCoreAsync(FolderRef folder, bool retry, CancellationToken ct)
    {

        var (byId, byPath) = await ReadAsync(c =>
        {
            Note? idMatch = null;
            if (folder.HasFileId)
            {
                idMatch = LoadNotes(c, null, "n.deleted_at IS NULL AND n.volume_serial = $v AND n.file_id = $f", cmd =>
                {
                    cmd.Parameters.AddWithValue("$v", folder.VolumeSerial);
                    cmd.Parameters.AddWithValue("$f", folder.FileId);
                }).FirstOrDefault();
            }

            var pathMatch = idMatch is not null ? null : LoadNotes(c, null, "n.deleted_at IS NULL AND n.path_norm = $p",
                cmd => cmd.Parameters.AddWithValue("$p", folder.PathNorm)).FirstOrDefault();
            return (idMatch, pathMatch);
        }, ct).ConfigureAwait(false);

        // Step 1: same file ID. The folder may have been renamed or moved, so follow it.
        if (byId is not null)
        {
            return SameBinding(byId.Folder, folder) ? byId : await TryRebindAsync(byId, folder, retry, ct).ConfigureAwait(false);
        }

        if (byPath is null)
        {
            return null; // step 3
        }

        // Step 2: same path, and one side isn't tracked by ID. Path decides (REQ-062).
        if (!byPath.Folder.HasFileId || !folder.HasFileId)
        {
            return SameBinding(byPath.Folder, folder) || !folder.HasFileId && byPath.Folder.HasFileId
                ? byPath
                : await TryRebindAsync(byPath, folder, retry, ct).ConfigureAwait(false);
        }

        // Step 2: same path, different ID. Another volume at the same path (a different drive at the
        // same letter) is a different folder (ADR-020). If the original folder is still alive
        // elsewhere, this is a different folder at the old path. Otherwise it was recreated here, so
        // re-bind (ADR-009).
        if (!string.Equals(byPath.Folder.VolumeSerial, folder.VolumeSerial, StringComparison.Ordinal))
        {
            return null;
        }

        var original = _options.Identity.Locate(byPath.Folder);
        return original.Status is LocateStatus.NotFound or LocateStatus.InRecycleBin
            ? await TryRebindAsync(byPath, folder, retry, ct).ConfigureAwait(false)
            : null;
    }

    // ───────────────────────────── notes: writes ─────────────────────────────

    public Task<Note> UpsertAsync(Note note, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(note);
        return WriteAsync(ctx => UpsertCore(ctx, note, preserveTimestamps: false), ct);
    }

    /// <summary>
    /// Import path (T1.7): keeps <c>created_at</c>/<c>updated_at</c> as given, and takes the incoming
    /// folder binding (import is an explicit restore, 03 § Export format). Every other caller keeps an
    /// existing note's binding and orphan state (ADR-020).
    /// </summary>
    internal Note UpsertCore(WriteContext ctx, Note note, bool preserveTimestamps)
    {
        var existing = ReadState(ctx, note.Id);
        if (existing is { DeletedAt: not null })
        {
            throw new InvalidOperationException($"Note {note.Id} is deleted. Restore it before editing.");
        }

        var created = StorageFormat.Truncate(existing?.CreatedAt ?? note.CreatedAt);
        var updated = preserveTimestamps ? StorageFormat.Truncate(note.UpdatedAt) : StorageFormat.Truncate(_options.Clock.GetUtcNow());
        if (updated < created)
        {
            updated = created;
        }

        var labels = ResolveLabels(ctx, note.Labels);
        var toSave = note with
        {
            Labels = labels.ToArray(),
            CreatedAt = created,
            UpdatedAt = updated,
            DeletedAt = null,

            // An existing note keeps its stored binding and orphan state: only relink / orphan
            // operations change them, so a stale editor copy can't undo a relink (03, ADR-020).
            Folder = existing is not null && !preserveTimestamps ? existing.Folder : note.Folder,
            OrphanedAt = existing is not null && !preserveTimestamps
                ? existing.OrphanedAt
                : note.OrphanedAt is { } o ? StorageFormat.Truncate(o) : null,
        };
        NoteValidator.EnsureValid(toSave);

        if (FindConflict(ctx, toSave.Id, toSave.Folder) is { } other)
        {
            throw new DuplicateFolderNoteException(other);
        }

        using (var cmd = ctx.Command("""
            INSERT INTO notes (id, path_norm, path_display, volume_serial, file_id, volume_kind, title, body_md, status,
                               accent_color, keep_until, review_on, safe_after, travel_with_folder, created_at, updated_at,
                               deleted_at, orphaned_at)
            VALUES ($id, $pn, $pd, $vs, $fi, $vk, $title, $body, $status, $accent, $keep, $review, $safe, $travel,
                    $created, $updated, NULL, $orphaned)
            ON CONFLICT(id) DO UPDATE SET
              path_norm = excluded.path_norm, path_display = excluded.path_display,
              volume_serial = excluded.volume_serial, file_id = excluded.file_id, volume_kind = excluded.volume_kind,
              title = excluded.title, body_md = excluded.body_md, status = excluded.status,
              accent_color = excluded.accent_color, keep_until = excluded.keep_until, review_on = excluded.review_on,
              safe_after = excluded.safe_after, travel_with_folder = excluded.travel_with_folder,
              updated_at = excluded.updated_at, orphaned_at = excluded.orphaned_at
            """))
        {
            BindFolder(cmd, toSave.Folder);
            cmd.Parameters.AddWithValue("$id", StorageFormat.Id(toSave.Id));
            cmd.Parameters.AddWithValue("$title", toSave.Title);
            cmd.Parameters.AddWithValue("$body", toSave.BodyMd);
            cmd.Parameters.AddWithValue("$status", toSave.Status.ToKey());
            cmd.Parameters.AddWithValue("$accent", (object?)toSave.AccentColor ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$keep", (object?)StorageFormat.Date(toSave.KeepUntil) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$review", (object?)StorageFormat.Date(toSave.ReviewOn) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$safe", (object?)StorageFormat.Date(toSave.SafeAfter) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$travel", toSave.TravelWithFolder ? 1 : 0);
            cmd.Parameters.AddWithValue("$created", StorageFormat.Timestamp(toSave.CreatedAt));
            cmd.Parameters.AddWithValue("$updated", StorageFormat.Timestamp(toSave.UpdatedAt));
            cmd.Parameters.AddWithValue("$orphaned", (object?)StorageFormat.Timestamp(toSave.OrphanedAt) ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }

        using (var del = ctx.Command("DELETE FROM note_labels WHERE note_id = $id"))
        {
            del.Parameters.AddWithValue("$id", StorageFormat.Id(toSave.Id));
            del.ExecuteNonQuery();
        }

        foreach (var label in labels)
        {
            using var ins = ctx.Command("INSERT INTO note_labels (note_id, label_id) VALUES ($n, $l)");
            ins.Parameters.AddWithValue("$n", StorageFormat.Id(toSave.Id));
            ins.Parameters.AddWithValue("$l", StorageFormat.Id(label.Id));
            ins.ExecuteNonQuery();
        }

        RefreshFts(ctx, toSave.Id);
        ctx.Raise(toSave.Id, existing is null ? NoteChangeKind.Created : NoteChangeKind.Updated, toSave.Folder.PathNorm);
        return toSave;
    }

    public Task DeleteAsync(Guid id, CancellationToken ct = default) => WriteAsync(ctx =>
    {
        var state = ReadState(ctx, id) ?? throw new KeyNotFoundException($"Note {id} doesn't exist.");
        if (state.DeletedAt is not null)
        {
            return 0;
        }

        using var cmd = ctx.Command("UPDATE notes SET deleted_at = $now WHERE id = $id");
        cmd.Parameters.AddWithValue("$id", StorageFormat.Id(id));
        cmd.Parameters.AddWithValue("$now", StorageFormat.Timestamp(_options.Clock.GetUtcNow()));
        cmd.ExecuteNonQuery();
        ctx.Raise(id, NoteChangeKind.Deleted, state.PathNorm);
        return 1;
    }, ct);

    public Task RestoreAsync(Guid id, CancellationToken ct = default) => WriteAsync(ctx =>
    {
        var state = ReadState(ctx, id) ?? throw new KeyNotFoundException($"Note {id} doesn't exist.");
        if (state.DeletedAt is null)
        {
            return 0;
        }

        if (FindConflict(ctx, id, state.Folder) is { } other)
        {
            throw new DuplicateFolderNoteException(other);
        }

        using var cmd = ctx.Command("UPDATE notes SET deleted_at = NULL WHERE id = $id");
        cmd.Parameters.AddWithValue("$id", StorageFormat.Id(id));
        cmd.ExecuteNonQuery();
        ctx.Raise(id, NoteChangeKind.Restored, state.PathNorm);
        return 1;
    }, ct);

    public Task<int> PurgeDeletedAsync(TimeSpan olderThan, CancellationToken ct = default) => WriteAsync(ctx =>
    {
        var cutoff = StorageFormat.Timestamp(_options.Clock.GetUtcNow() - olderThan);
        var victims = new List<(string Id, string PathNorm)>();
        using (var sel = ctx.Command("SELECT id, path_norm FROM notes WHERE deleted_at IS NOT NULL AND deleted_at <= $cut"))
        {
            sel.Parameters.AddWithValue("$cut", cutoff);
            using var r = sel.ExecuteReader();
            while (r.Read())
            {
                victims.Add((r.GetString(0), r.GetString(1)));
            }
        }

        foreach (var (id, pathNorm) in victims)
        {
            using var cmd = ctx.Command("""
                DELETE FROM notes_fts WHERE rowid = (SELECT rowid FROM notes WHERE id = $id);
                DELETE FROM notes WHERE id = $id;
                """);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
            ctx.Raise(StorageFormat.ParseId(id), NoteChangeKind.Purged, pathNorm);
        }

        return victims.Count;
    }, ct);

    public Task<Note> RelinkAsync(Guid id, FolderRef newFolder, FolderRef? expectedFolder, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(newFolder);
        return WriteAsync(ctx => RebindCore(ctx, id, newFolder, expectedFolder), ct);
    }

    public Task<bool> SetOrphanedAsync(Guid id, bool orphaned, FolderRef? expectedFolder, CancellationToken ct = default) => WriteAsync(ctx =>
    {
        var state = ReadState(ctx, id) ?? throw new KeyNotFoundException($"Note {id} doesn't exist.");
        if (expectedFolder is not null && (state.DeletedAt is not null || !state.Folder.SameBindingAs(expectedFolder)))
        {
            throw new NoteBindingChangedException(id);
        }

        if (state.DeletedAt is not null || (state.OrphanedAt is not null) == orphaned)
        {
            return false;
        }

        using var cmd = ctx.Command("UPDATE notes SET orphaned_at = $at WHERE id = $id");
        cmd.Parameters.AddWithValue("$id", StorageFormat.Id(id));
        cmd.Parameters.AddWithValue("$at", orphaned ? StorageFormat.Timestamp(_options.Clock.GetUtcNow()) : DBNull.Value);
        cmd.ExecuteNonQuery();
        ctx.Raise(id, NoteChangeKind.OrphanStateChanged, state.PathNorm);
        return true;
    }, ct);

    private async Task<Note?> TryRebindAsync(Note note, FolderRef folder, bool retry, CancellationToken ct)
    {
        try
        {
            // Conditional on the binding we read (ADR-020): a relink in between wins.
            return await WriteAsync(ctx => RebindCore(ctx, note.Id, folder, note.Folder), ct).ConfigureAwait(false);
        }
        catch (DuplicateFolderNoteException)
        {
            return note; // another live note holds the new path. Leave both alone; the orphan scan reports it.
        }
        catch (Exception ex) when (ex is NoteBindingChangedException or KeyNotFoundException)
        {
            // Changed under us: look again, once, against the new state.
            return retry ? await GetByFolderCoreAsync(folder, retry: false, ct).ConfigureAwait(false) : null;
        }
    }

    private static Note RebindCore(WriteContext ctx, Guid id, FolderRef folder, FolderRef? expected)
    {
        var state = ReadState(ctx, id);
        if (expected is not null && state is not null && (state.DeletedAt is not null || !state.Folder.SameBindingAs(expected)))
        {
            throw new NoteBindingChangedException(id);
        }

        if (state is null || state.DeletedAt is not null)
        {
            throw new KeyNotFoundException($"Note {id} doesn't exist or is deleted.");
        }

        if (FindConflict(ctx, id, folder) is { } other)
        {
            throw new DuplicateFolderNoteException(other);
        }

        using (var cmd = ctx.Command("""
            UPDATE notes SET path_norm = $pn, path_display = $pd, volume_serial = $vs, file_id = $fi, volume_kind = $vk,
                             orphaned_at = NULL
            WHERE id = $id
            """))
        {
            BindFolder(cmd, folder);
            cmd.Parameters.AddWithValue("$id", StorageFormat.Id(id));
            cmd.ExecuteNonQuery();
        }

        RefreshFts(ctx, id);
        ctx.Raise(id, NoteChangeKind.Relinked, folder.PathNorm);
        return LoadNotes(ctx.Connection, ctx.Transaction, "n.id = $id", c => c.Parameters.AddWithValue("$id", StorageFormat.Id(id))).Single();
    }

    // ───────────────────────────── labels ─────────────────────────────

    public Task<IReadOnlyList<Label>> ListLabelsAsync(CancellationToken ct = default) =>
        ReadAsync<IReadOnlyList<Label>>(c => LoadLabels(c, null), ct);

    public Task<IReadOnlyDictionary<Guid, int>> CountNotesByLabelAsync(CancellationToken ct = default) =>
        ReadAsync<IReadOnlyDictionary<Guid, int>>(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                SELECT nl.label_id, count(*) FROM note_labels nl
                JOIN notes n ON n.id = nl.note_id
                WHERE n.deleted_at IS NULL
                GROUP BY nl.label_id
                """;
            using var r = cmd.ExecuteReader();
            var counts = new Dictionary<Guid, int>();
            while (r.Read())
            {
                counts[StorageFormat.ParseId(r.GetString(0))] = r.GetInt32(1);
            }

            return counts;
        }, ct);

    public Task<Label> CreateLabelAsync(string name, string color, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        var label = Label.Create(name, color);
        NoteValidator.EnsureValid(label);
        return WriteAsync(ctx =>
        {
            if (LoadLabels(ctx.Connection, ctx.Transaction).FirstOrDefault(l => Label.NameComparer.Equals(l.Name, label.Name)) is { } dup)
            {
                throw new DuplicateLabelException(label.Name, dup.Id);
            }

            InsertLabel(ctx, label);
            ctx.LabelsChanged = true;
            return label;
        }, ct);
    }

    public Task<Label> UpdateLabelAsync(Label label, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(label);
        var clean = label with { Name = label.Name?.Trim() ?? "" };
        NoteValidator.EnsureValid(clean);
        return WriteAsync(ctx =>
        {
            var all = LoadLabels(ctx.Connection, ctx.Transaction);
            var current = all.FirstOrDefault(l => l.Id == clean.Id) ?? throw new KeyNotFoundException($"Label {clean.Id} doesn't exist.");
            if (all.FirstOrDefault(l => l.Id != clean.Id && Label.NameComparer.Equals(l.Name, clean.Name)) is { } dup)
            {
                throw new DuplicateLabelException(clean.Name, dup.Id);
            }

            using (var cmd = ctx.Command("UPDATE labels SET name = $name, color = $color WHERE id = $id"))
            {
                cmd.Parameters.AddWithValue("$id", StorageFormat.Id(clean.Id));
                cmd.Parameters.AddWithValue("$name", clean.Name);
                cmd.Parameters.AddWithValue("$color", clean.Color);
                cmd.ExecuteNonQuery();
            }

            // REQ-012: renaming updates every note. The junction table already points at the label,
            // but each note's search text (and its listeners) need the new name.
            foreach (var (noteId, pathNorm) in NotesWithLabel(ctx, clean.Id))
            {
                if (!string.Equals(current.Name, clean.Name, StringComparison.Ordinal))
                {
                    RefreshFts(ctx, noteId);
                }

                ctx.Raise(noteId, NoteChangeKind.Updated, pathNorm);
            }

            ctx.LabelsChanged = true;
            return clean;
        }, ct);
    }

    public Task DeleteLabelAsync(Guid id, CancellationToken ct = default) => WriteAsync(ctx =>
    {
        var affected = NotesWithLabel(ctx, id);
        using (var cmd = ctx.Command("DELETE FROM labels WHERE id = $id"))
        {
            cmd.Parameters.AddWithValue("$id", StorageFormat.Id(id));
            if (cmd.ExecuteNonQuery() == 0)
            {
                throw new KeyNotFoundException($"Label {id} doesn't exist.");
            }
        }

        foreach (var (noteId, pathNorm) in affected)
        {
            RefreshFts(ctx, noteId);
            ctx.Raise(noteId, NoteChangeKind.Updated, pathNorm);
        }

        ctx.LabelsChanged = true;
        return 0;
    }, ct);

    // ───────────────────────────── helpers ─────────────────────────────

    private static bool SameBinding(FolderRef a, FolderRef b) =>
        string.Equals(a.PathNorm, b.PathNorm, StringComparison.Ordinal)
        && string.Equals(a.PathDisplay, b.PathDisplay, StringComparison.Ordinal)
        && string.Equals(a.VolumeSerial, b.VolumeSerial, StringComparison.Ordinal)
        && string.Equals(a.FileId, b.FileId, StringComparison.Ordinal)
        && a.VolumeKind == b.VolumeKind;

    private static void BindFolder(SqliteCommand cmd, FolderRef f)
    {
        var tracked = f.HasFileId;
        cmd.Parameters.AddWithValue("$pn", f.PathNorm);
        cmd.Parameters.AddWithValue("$pd", f.PathDisplay);
        cmd.Parameters.AddWithValue("$vs", tracked ? f.VolumeSerial : DBNull.Value);
        cmd.Parameters.AddWithValue("$fi", tracked ? f.FileId : DBNull.Value);
        cmd.Parameters.AddWithValue("$vk", f.VolumeKind.ToKey());
    }

    private sealed record RowState(string PathNorm, DateTimeOffset CreatedAt, DateTimeOffset? DeletedAt, DateTimeOffset? OrphanedAt, FolderRef Folder);

    private static RowState? ReadState(WriteContext ctx, Guid id)
    {
        using var cmd = ctx.Command("SELECT path_norm, path_display, volume_serial, file_id, volume_kind, created_at, deleted_at, orphaned_at FROM notes WHERE id = $id");
        cmd.Parameters.AddWithValue("$id", StorageFormat.Id(id));
        using var r = cmd.ExecuteReader();
        if (!r.Read())
        {
            return null;
        }

        var folder = new FolderRef(r.GetString(0), r.GetString(1), NullableString(r, 2), NullableString(r, 3), VolumeKindExtensions.ParseVolumeKind(r.GetString(4)));
        return new RowState(folder.PathNorm, StorageFormat.ParseTimestamp(r.GetString(5)),
            StorageFormat.ParseTimestampOrNull(NullableString(r, 6)), StorageFormat.ParseTimestampOrNull(NullableString(r, 7)), folder);
    }

    /// <summary>The id of another live note bound to the same path or file ID, if any (REQ-001).</summary>
    private static Guid? FindConflict(WriteContext ctx, Guid selfId, FolderRef folder)
    {
        // Two arms so each one can use its partial unique index (an OR here forced a full scan).
        using var cmd = ctx.Command("""
            SELECT id FROM notes WHERE path_norm = $pn AND deleted_at IS NULL AND id <> $self
            UNION ALL
            SELECT id FROM notes WHERE volume_serial = $vs AND file_id = $fi AND deleted_at IS NULL AND file_id IS NOT NULL AND id <> $self
            LIMIT 1
            """);
        BindFolder(cmd, folder);
        cmd.Parameters.AddWithValue("$self", StorageFormat.Id(selfId));
        return cmd.ExecuteScalar() is string s ? StorageFormat.ParseId(s) : null;
    }

    private static List<Label> ResolveLabels(WriteContext ctx, IReadOnlyList<Label>? requested)
    {
        var result = new List<Label>();
        if (requested is null || requested.Count == 0)
        {
            return result;
        }

        var all = LoadLabels(ctx.Connection, ctx.Transaction);
        foreach (var want in requested)
        {
            // Match by id first (a stale name after a rename still resolves), then by name, else create (REQ-011).
            var match = all.FirstOrDefault(l => l.Id == want.Id)
                ?? all.FirstOrDefault(l => Label.NameComparer.Equals(l.Name, want.Name?.Trim()));
            if (match is null)
            {
                match = new Label(want.Id == Guid.Empty ? Guid.NewGuid() : want.Id, want.Name?.Trim() ?? "", want.Color);
                NoteValidator.EnsureValid(match);
                InsertLabel(ctx, match);
                all.Add(match);
                ctx.LabelsChanged = true;
            }

            if (!result.Any(l => l.Id == match.Id))
            {
                result.Add(match);
            }
        }

        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    private static void InsertLabel(WriteContext ctx, Label label)
    {
        using var cmd = ctx.Command("INSERT INTO labels (id, name, color) VALUES ($id, $name, $color)");
        cmd.Parameters.AddWithValue("$id", StorageFormat.Id(label.Id));
        cmd.Parameters.AddWithValue("$name", label.Name);
        cmd.Parameters.AddWithValue("$color", label.Color);
        cmd.ExecuteNonQuery();
    }

    private static List<(Guid NoteId, string PathNorm)> NotesWithLabel(WriteContext ctx, Guid labelId)
    {
        using var cmd = ctx.Command("SELECT n.id, n.path_norm FROM note_labels nl JOIN notes n ON n.id = nl.note_id WHERE nl.label_id = $l");
        cmd.Parameters.AddWithValue("$l", StorageFormat.Id(labelId));
        using var r = cmd.ExecuteReader();
        var list = new List<(Guid, string)>();
        while (r.Read())
        {
            list.Add((StorageFormat.ParseId(r.GetString(0)), r.GetString(1)));
        }

        return list;
    }

    private static void RefreshFts(WriteContext ctx, Guid noteId)
    {
        using var cmd = ctx.Command("""
            DELETE FROM notes_fts WHERE rowid = (SELECT rowid FROM notes WHERE id = $id);
            INSERT INTO notes_fts (rowid, title, body_md, labels, path_display)
            SELECT n.rowid, n.title, n.body_md,
                   coalesce((SELECT group_concat(l.name, ' ') FROM note_labels nl JOIN labels l ON l.id = nl.label_id
                             WHERE nl.note_id = n.id), ''),
                   n.path_display
            FROM notes n WHERE n.id = $id;
            """);
        cmd.Parameters.AddWithValue("$id", StorageFormat.Id(noteId));
        cmd.ExecuteNonQuery();
    }

    internal static List<Label> LoadLabels(SqliteConnection c, SqliteTransaction? tx)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT id, name, color FROM labels ORDER BY name COLLATE NOCASE";
        using var r = cmd.ExecuteReader();
        var list = new List<Label>();
        while (r.Read())
        {
            list.Add(new Label(StorageFormat.ParseId(r.GetString(0)), r.GetString(1), r.GetString(2)));
        }

        return list;
    }

    internal static List<Note> QueryNotes(SqliteConnection c, NoteQuery query)
    {
        var labels = query.Labels.Count > 0 ? LoadLabels(c, null) : [];
        return LoadNotes(c, null, NoteFilterSql.Where(query, labels, out var bind), bind, NoteFilterSql.OrderBy(query.Sort), query.Limit, query.Offset);
    }

    /// <summary>Loads live notes by id on an open read connection (search hits).</summary>
    internal static Dictionary<Guid, Note> QueryNotesForSearch(SqliteConnection c, IReadOnlyList<Guid> ids) =>
        QueryNotes(c, new NoteQuery { Ids = ids, State = NoteListState.Active }).ToDictionary(n => n.Id);

    internal static List<Note> LoadNotes(
        SqliteConnection c, SqliteTransaction? tx, string where, Action<SqliteCommand> bind,
        string orderBy = "n.rowid", int? limit = null, int offset = 0)
    {
        var notes = new List<Note>();
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $"SELECT {NoteColumns} FROM notes n WHERE {where} ORDER BY {orderBy} LIMIT $limit OFFSET $offset";
            bind(cmd);
            cmd.Parameters.AddWithValue("$limit", limit ?? -1);
            cmd.Parameters.AddWithValue("$offset", offset);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                notes.Add(ReadNote(r));
            }
        }

        if (notes.Count == 0)
        {
            return notes;
        }

        var byNote = new Dictionary<Guid, List<Label>>();
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                SELECT nl.note_id, l.id, l.name, l.color FROM note_labels nl JOIN labels l ON l.id = nl.label_id
                WHERE nl.note_id IN (SELECT value FROM json_each($ids))
                ORDER BY l.name COLLATE NOCASE
                """;
            cmd.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(notes.Select(n => StorageFormat.Id(n.Id))));
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var id = StorageFormat.ParseId(r.GetString(0));
                if (!byNote.TryGetValue(id, out var list))
                {
                    byNote[id] = list = [];
                }

                list.Add(new Label(StorageFormat.ParseId(r.GetString(1)), r.GetString(2), r.GetString(3)));
            }
        }

        for (var i = 0; i < notes.Count; i++)
        {
            if (byNote.TryGetValue(notes[i].Id, out var list))
            {
                notes[i] = notes[i] with { Labels = list.ToArray() };
            }
        }

        return notes;
    }

    private static Note ReadNote(SqliteDataReader r) => new()
    {
        Id = StorageFormat.ParseId(r.GetString(1)),
        Folder = new FolderRef(r.GetString(2), r.GetString(3), NullableString(r, 4), NullableString(r, 5), VolumeKindExtensions.ParseVolumeKind(r.GetString(6))),
        Title = r.GetString(7),
        BodyMd = r.GetString(8),
        Status = NoteStatusExtensions.ParseKey(r.GetString(9)),
        AccentColor = NullableString(r, 10),
        KeepUntil = StorageFormat.ParseDate(NullableString(r, 11)),
        ReviewOn = StorageFormat.ParseDate(NullableString(r, 12)),
        SafeAfter = StorageFormat.ParseDate(NullableString(r, 13)),
        TravelWithFolder = r.GetInt64(14) != 0,
        CreatedAt = StorageFormat.ParseTimestamp(r.GetString(15)),
        UpdatedAt = StorageFormat.ParseTimestamp(r.GetString(16)),
        DeletedAt = StorageFormat.ParseTimestampOrNull(NullableString(r, 17)),
        OrphanedAt = StorageFormat.ParseTimestampOrNull(NullableString(r, 18)),
    };

    private static string? NullableString(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static void Exec(SqliteConnection c, SqliteTransaction? tx, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static int UserVersion(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA user_version";
        return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void Migrate(SqliteConnection c, SqliteNoteStoreOptions options)
    {
        var migrations = options.Migrations ?? Schema.Migrations;
        var target = migrations[^1].Version;
        var current = UserVersion(c);
        if (current > target)
        {
            throw new UnsupportedSchemaException(current, target);
        }

        if (current == target)
        {
            return;
        }

        if (current >= 1)
        {
            BackupDatabase(c, options, current);
        }

        foreach (var m in migrations.Where(m => m.Version > current).OrderBy(m => m.Version))
        {
            using var tx = c.BeginTransaction();
            m.Apply(c, tx);
            Exec(c, tx, $"PRAGMA user_version = {m.Version:D}");
            tx.Commit();
        }
    }

    private static void BackupDatabase(SqliteConnection c, SqliteNoteStoreOptions options, int fromVersion)
    {
        var dir = options.BackupDirectory ?? Path.Combine(Path.GetDirectoryName(options.DatabasePath)!, "backups");
        Directory.CreateDirectory(dir);
        var stamp = options.Clock.GetUtcNow().UtcDateTime.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var path = Path.Combine(dir, $"notes-v{fromVersion}-{stamp}.db");
        using var dest = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        dest.Open();
        c.BackupDatabase(dest);
    }
}
