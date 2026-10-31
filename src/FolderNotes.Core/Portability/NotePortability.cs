using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using FolderNotes.Core.Domain;
using FolderNotes.Core.Identity;
using FolderNotes.Core.Storage;

namespace FolderNotes.Core.Portability;

/// <summary>The export file (03 § Export format, REQ-070).</summary>
public sealed record ExportDocument
{
    public const string SchemaId = "foldernotes-export/1";

    public string Schema { get; init; } = SchemaId;
    public string? ExportedAt { get; init; }
    public IReadOnlyList<ExportLabel> Labels { get; init; } = [];
    public IReadOnlyList<ExportNote> Notes { get; init; } = [];
}

public sealed record ExportLabel(string Name, string Color);

public sealed record ExportNote
{
    public Guid Id { get; init; }
    public string PathDisplay { get; init; } = "";
    public string Title { get; init; } = "";
    public string BodyMd { get; init; } = "";
    public string Status { get; init; } = "none";
    public string? AccentColor { get; init; }
    public IReadOnlyList<string> Labels { get; init; } = [];
    public DateOnly? KeepUntil { get; init; }
    public DateOnly? ReviewOn { get; init; }
    public DateOnly? SafeAfter { get; init; }
    public bool TravelWithFolder { get; init; }
    public string CreatedAt { get; init; } = "";
    public string UpdatedAt { get; init; } = "";
}

public enum ImportConflictPolicy
{
    /// <summary>Replace only if the imported <c>updated_at</c> is newer (the sidecar rule).</summary>
    NewerWins,
    Overwrite,
    KeepExisting,
}

public sealed record ImportIssue(int Index, string? Path, string Message);

public sealed record ImportReport(int Created, int Updated, int Unchanged, IReadOnlyList<ImportIssue> Skipped);

public sealed class ExportFormatException : Exception
{
    public ExportFormatException()
    {
    }

    public ExportFormatException(string message)
        : base(message)
    {
    }

    public ExportFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>REQ-070 export / import, matching by path.</summary>
public sealed class NotePortability(SqliteNoteStore store, IFolderIdentityService identity)
{
    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // keep non-ASCII readable in the file
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public static string DefaultFileName(DateOnly date) =>
        "foldernotes-export-" + date.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".json";

    /// <summary>All live notes, or only <paramref name="noteIds"/>.</summary>
    public async Task<ExportDocument> ExportAsync(IReadOnlyCollection<Guid>? noteIds = null, CancellationToken ct = default)
    {
        var notes = await store.ListAsync(new NoteQuery { Ids = noteIds?.ToList(), Sort = NoteSort.Path }, ct).ConfigureAwait(false);
        var labels = await store.ListLabelsAsync(ct).ConfigureAwait(false);
        if (noteIds is not null)
        {
            var used = notes.SelectMany(n => n.Labels).Select(l => l.Id).ToHashSet();
            labels = [.. labels.Where(l => used.Contains(l.Id))];
        }

        return new ExportDocument
        {
            ExportedAt = StorageFormat.Timestamp(store.Clock.GetUtcNow()),
            Labels = [.. labels.Select(l => new ExportLabel(l.Name, l.Color))],
            Notes = [.. notes.Select(ToExport)],
        };
    }

    public static async Task WriteAsync(Stream stream, ExportDocument document, CancellationToken ct = default) =>
        await JsonSerializer.SerializeAsync(stream, document, s_json, ct).ConfigureAwait(false);

    public static async Task<ExportDocument> ReadAsync(Stream stream, CancellationToken ct = default)
    {
        ExportDocument? doc;
        try
        {
            doc = await JsonSerializer.DeserializeAsync<ExportDocument>(stream, s_json, ct).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new ExportFormatException("The file isn't a valid Folder Notes export: " + ex.Message, ex);
        }

        if (doc is null || doc.Schema != ExportDocument.SchemaId)
        {
            throw new ExportFormatException($"Unsupported export schema '{doc?.Schema}'. Expected '{ExportDocument.SchemaId}'.");
        }

        return doc with { Labels = doc.Labels ?? [], Notes = doc.Notes ?? [] };
    }

    public async Task<ImportReport> ImportAsync(ExportDocument document, ImportConflictPolicy policy = ImportConflictPolicy.NewerWins, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Schema != ExportDocument.SchemaId)
        {
            throw new ExportFormatException($"Unsupported export schema '{document.Schema}'.");
        }

        // Resolve folders (file system access) before entering the writer.
        var skipped = new List<ImportIssue>();
        var prepared = new List<(int Index, Note Note)>();
        var colors = document.Labels
            .Where(l => !string.IsNullOrWhiteSpace(l.Name))
            .GroupBy(l => l.Name.Trim(), Label.NameComparer)
            .ToDictionary(g => g.Key, g => g.Last().Color, Label.NameComparer);

        for (var i = 0; i < document.Notes.Count; i++)
        {
            var e = document.Notes[i];
            if (FromExport(e, colors, out var note, out var error) is false)
            {
                skipped.Add(new ImportIssue(i, e?.PathDisplay, error!));
                continue;
            }

            prepared.Add((i, note!));
        }

        var (created, updated, unchanged) = await store.WriteAsync(ctx =>
        {
            ApplyLabelColors(ctx, document.Labels);
            int c = 0, u = 0, same = 0;
            foreach (var (index, incoming) in prepared)
            {
                using (var sp = ctx.Command("SAVEPOINT import_note"))
                {
                    sp.ExecuteNonQuery();
                }

                try
                {
                    var outcome = ImportOne(ctx, incoming, policy);
                    if (outcome == 0)
                    {
                        c++;
                    }
                    else if (outcome == 1)
                    {
                        u++;
                    }
                    else
                    {
                        same++;
                    }

                    using var rel = ctx.Command("RELEASE import_note");
                    rel.ExecuteNonQuery();
                }
                catch (Exception ex) when (ex is NoteValidationException or DuplicateFolderNoteException or InvalidOperationException)
                {
                    using (var rb = ctx.Command("ROLLBACK TO import_note; RELEASE import_note"))
                    {
                        rb.ExecuteNonQuery();
                    }

                    ctx.DropEventsFor(incoming.Id);
                    skipped.Add(new ImportIssue(index, incoming.Folder.PathDisplay, ex.Message));
                }
            }

            return (c, u, same);
        }, ct).ConfigureAwait(false);

        skipped.Sort((a, b) => a.Index.CompareTo(b.Index));
        return new ImportReport(created, updated, unchanged, skipped.ToArray());
    }

    /// <summary>0 = created, 1 = updated, 2 = left unchanged by the policy.</summary>
    private static int ImportOne(SqliteNoteStore.WriteContext ctx, Note incoming, ImportConflictPolicy policy)
    {
        var match = LiveIdByPath(ctx, incoming.Folder.PathNorm) ?? (IsLive(ctx, incoming.Id) ? incoming.Id : null);
        if (match is { } existingId)
        {
            var existingUpdated = UpdatedAt(ctx, existingId);
            var replace = policy switch
            {
                ImportConflictPolicy.Overwrite => true,
                ImportConflictPolicy.KeepExisting => false,
                _ => incoming.UpdatedAt > existingUpdated,
            };
            if (!replace)
            {
                return 2;
            }

            ctx.Store.UpsertCore(ctx, incoming with { Id = existingId }, preserveTimestamps: true);
            return 1;
        }

        var id = Exists(ctx, incoming.Id) ? Guid.NewGuid() : incoming.Id;
        ctx.Store.UpsertCore(ctx, incoming with { Id = id }, preserveTimestamps: true);
        return 0;
    }

    private bool FromExport(ExportNote? e, Dictionary<string, string> colors, out Note? note, out string? error)
    {
        note = null;
        error = null;
        if (e is null)
        {
            error = "Empty entry.";
            return false;
        }

        if (!identity.TryResolve(e.PathDisplay ?? "", out var folder))
        {
            error = $"'{e.PathDisplay}' isn't a folder path.";
            return false;
        }

        if (!NoteStatusExtensions.TryParseKey(e.Status, out var status))
        {
            error = $"Unknown status '{e.Status}'.";
            return false;
        }

        DateTimeOffset created, updated;
        try
        {
            created = StorageFormat.ParseTimestamp(e.CreatedAt);
            updated = StorageFormat.ParseTimestamp(e.UpdatedAt);
        }
        catch (FormatException)
        {
            if (!DateTimeOffset.TryParse(e.CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out created)
                || !DateTimeOffset.TryParse(e.UpdatedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out updated))
            {
                error = "Bad created_at / updated_at timestamp.";
                return false;
            }
        }

        note = new Note
        {
            Id = e.Id == Guid.Empty ? Guid.NewGuid() : e.Id,
            Folder = folder,
            Title = e.Title ?? "",
            BodyMd = e.BodyMd ?? "",
            Status = status,
            AccentColor = e.AccentColor,
            Labels = [.. (e.Labels ?? []).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n =>
                Label.Create(n, colors.TryGetValue(n.Trim(), out var c) && Palette.IsValid(c) ? c : "blue"))],
            KeepUntil = e.KeepUntil,
            ReviewOn = e.ReviewOn,
            SafeAfter = e.SafeAfter,
            TravelWithFolder = e.TravelWithFolder,
            CreatedAt = created,
            UpdatedAt = updated,
        };

        var errors = NoteValidator.Validate(note);
        if (errors.Count > 0)
        {
            error = string.Join("; ", errors.Select(x => x.Message));
            note = null;
            return false;
        }

        return true;
    }

    private static ExportNote ToExport(Note n) => new()
    {
        Id = n.Id,
        PathDisplay = n.Folder.PathDisplay,
        Title = n.Title,
        BodyMd = n.BodyMd,
        Status = n.Status.ToKey(),
        AccentColor = n.AccentColor,
        Labels = [.. n.Labels.Select(l => l.Name)],
        KeepUntil = n.KeepUntil,
        ReviewOn = n.ReviewOn,
        SafeAfter = n.SafeAfter,
        TravelWithFolder = n.TravelWithFolder,
        CreatedAt = StorageFormat.Timestamp(n.CreatedAt),
        UpdatedAt = StorageFormat.Timestamp(n.UpdatedAt),
    };

    /// <summary>Creates missing labels, and gives existing ones the exported color.</summary>
    private static void ApplyLabelColors(SqliteNoteStore.WriteContext ctx, IReadOnlyList<ExportLabel> labels)
    {
        var all = SqliteNoteStore.LoadLabels(ctx.Connection, ctx.Transaction);
        foreach (var l in labels)
        {
            var candidate = new Label(Guid.NewGuid(), l.Name?.Trim() ?? "", l.Color);
            if (NoteValidator.ValidateLabel(candidate).Count > 0)
            {
                continue; // a bad label entry just doesn't get imported; notes fall back to a default color
            }

            var existing = all.FirstOrDefault(x => Label.NameComparer.Equals(x.Name, candidate.Name));
            using var cmd = existing is null
                ? ctx.Command("INSERT INTO labels (id, name, color) VALUES ($id, $name, $color)")
                : ctx.Command("UPDATE labels SET color = $color WHERE id = $id AND color <> $color");
            cmd.Parameters.AddWithValue("$id", StorageFormat.Id(existing?.Id ?? candidate.Id));
            cmd.Parameters.AddWithValue("$name", candidate.Name);
            cmd.Parameters.AddWithValue("$color", candidate.Color);
            if (cmd.ExecuteNonQuery() > 0)
            {
                ctx.LabelsChanged = true;
            }

            if (existing is null)
            {
                all.Add(candidate);
            }
        }
    }

    private static Guid? LiveIdByPath(SqliteNoteStore.WriteContext ctx, string pathNorm)
    {
        using var cmd = ctx.Command("SELECT id FROM notes WHERE path_norm = $p AND deleted_at IS NULL");
        cmd.Parameters.AddWithValue("$p", pathNorm);
        return cmd.ExecuteScalar() is string s ? StorageFormat.ParseId(s) : null;
    }

    private static bool IsLive(SqliteNoteStore.WriteContext ctx, Guid id)
    {
        using var cmd = ctx.Command("SELECT 1 FROM notes WHERE id = $id AND deleted_at IS NULL");
        cmd.Parameters.AddWithValue("$id", StorageFormat.Id(id));
        return cmd.ExecuteScalar() is not null;
    }

    private static bool Exists(SqliteNoteStore.WriteContext ctx, Guid id)
    {
        using var cmd = ctx.Command("SELECT 1 FROM notes WHERE id = $id");
        cmd.Parameters.AddWithValue("$id", StorageFormat.Id(id));
        return cmd.ExecuteScalar() is not null;
    }

    private static DateTimeOffset UpdatedAt(SqliteNoteStore.WriteContext ctx, Guid id)
    {
        using var cmd = ctx.Command("SELECT updated_at FROM notes WHERE id = $id");
        cmd.Parameters.AddWithValue("$id", StorageFormat.Id(id));
        return StorageFormat.ParseTimestamp((string)cmd.ExecuteScalar()!);
    }
}
