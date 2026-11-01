using FolderNotes.Core.Domain;

namespace FolderNotes.Core.Storage;

public enum NoteChangeKind
{
    Created,
    Updated,
    Deleted,
    Restored,
    Purged,

    /// <summary>The note was re-bound to a folder (moved, recreated, or relinked by the user).</summary>
    Relinked,

    /// <summary><see cref="Note.OrphanedAt"/> was set or cleared.</summary>
    OrphanStateChanged,
}

/// <summary>Raised after commit, on the writer task (ADR-011).</summary>
public sealed class NoteChangedEventArgs(Guid noteId, NoteChangeKind kind, string pathNorm) : EventArgs
{
    public Guid NoteId { get; } = noteId;
    public NoteChangeKind Kind { get; } = kind;
    public string PathNorm { get; } = pathNorm;
}

public enum NoteListState
{
    /// <summary>Not deleted (orphaned notes included).</summary>
    Active,

    /// <summary>Not deleted and orphaned (REQ-061 "Orphaned" view).</summary>
    Orphaned,

    /// <summary>Soft-deleted, waiting for purge.</summary>
    Deleted,

    All,
}

public enum NoteSort
{
    UpdatedDesc,
    Title,
    DueDate,
    Path,
}

/// <summary>Inclusive date range. A null bound is open.</summary>
public sealed record DateRange(DateOnly? From, DateOnly? To);

/// <summary>Filter + sort for <see cref="INoteRepository.ListAsync"/> and search (REQ-052, REQ-054).</summary>
public sealed record NoteQuery
{
    public NoteListState State { get; init; } = NoteListState.Active;

    /// <summary>Label names. A note must have every one of them (case-insensitive).</summary>
    public IReadOnlyList<string> Labels { get; init; } = [];

    /// <summary>A note must have one of these statuses. Empty = any.</summary>
    public IReadOnlyList<NoteStatus> Statuses { get; init; } = [];

    /// <summary><c>path_norm</c> keys. A note must be at or under one of them. Empty = anywhere.</summary>
    public IReadOnlyList<string> PathsUnder { get; init; } = [];

    /// <summary>Filter on <see cref="Note.DueDate"/> (earliest of review on / safe after).</summary>
    public DateRange? Due { get; init; }

    /// <summary>Filter on <see cref="Note.KeepUntil"/>.</summary>
    public DateRange? Keep { get; init; }

    /// <summary>Restrict to these ids (used by search to load its hits).</summary>
    public IReadOnlyList<Guid>? Ids { get; init; }

    public NoteSort Sort { get; init; } = NoteSort.UpdatedDesc;
    public int? Limit { get; init; }
    public int Offset { get; init; }

    public static NoteQuery All { get; } = new();
}

/// <summary>REQ-001: the folder already has a live note.</summary>
public sealed class DuplicateFolderNoteException : Exception
{
    public DuplicateFolderNoteException(Guid existingNoteId)
        : base($"The folder already has a note ({existingNoteId}).")
    {
        ExistingNoteId = existingNoteId;
    }

    public DuplicateFolderNoteException()
    {
    }

    public DuplicateFolderNoteException(string message)
        : base(message)
    {
    }

    public DuplicateFolderNoteException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public Guid ExistingNoteId { get; }
}

/// <summary>
/// The note's folder binding isn't what the caller expected: it was relinked, re-bound or deleted
/// in the meantime (03 § Relink rules, ADR-020). Nothing was changed.
/// </summary>
public sealed class NoteBindingChangedException : Exception
{
    public NoteBindingChangedException(Guid noteId)
        : base($"Note {noteId} was relinked or changed in the meantime.")
    {
        NoteId = noteId;
    }

    public NoteBindingChangedException()
    {
    }

    public NoteBindingChangedException(string message)
        : base(message)
    {
    }

    public NoteBindingChangedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public Guid NoteId { get; }
}

/// <summary>REQ-010: a label with that name (ignoring case) already exists.</summary>
public sealed class DuplicateLabelException : Exception
{
    public DuplicateLabelException(string name, Guid existingId)
        : base($"A label named '{name}' already exists.")
    {
        ExistingLabelId = existingId;
    }

    public DuplicateLabelException()
    {
    }

    public DuplicateLabelException(string message)
        : base(message)
    {
    }

    public DuplicateLabelException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public Guid ExistingLabelId { get; }
}

/// <summary>The database was written by a newer app version (03 § Schema versions).</summary>
public sealed class UnsupportedSchemaException : Exception
{
    public UnsupportedSchemaException(int found, int supported)
        : base($"The database schema version {found} is newer than this app supports ({supported}).")
    {
        Found = found;
        Supported = supported;
    }

    public UnsupportedSchemaException()
    {
    }

    public UnsupportedSchemaException(string message)
        : base(message)
    {
    }

    public UnsupportedSchemaException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public int Found { get; }
    public int Supported { get; }
}

public interface INoteRepository
{
    /// <summary>ADR-009 lookup (03 § Lookup algorithm), including re-binding moved or recreated folders.</summary>
    Task<Note?> GetByFolderAsync(FolderRef folder, CancellationToken ct = default);

    /// <summary>Includes soft-deleted notes (<see cref="Note.IsDeleted"/>) so Undo can show them.</summary>
    Task<Note?> GetByIdAsync(Guid id, CancellationToken ct = default);

    Task<Note> UpsertAsync(Note note, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    Task RestoreAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Note>> ListAsync(NoteQuery query, CancellationToken ct = default);
    Task<int> PurgeDeletedAsync(TimeSpan olderThan, CancellationToken ct = default);

    /// <summary>
    /// Binds the note to another folder (REQ-061 Relink…) and clears the orphan flag, without touching
    /// its content or <c>updated_at</c>. With <paramref name="expectedFolder"/>, the stored binding must
    /// still equal it, else <see cref="NoteBindingChangedException"/> and nothing changes (ADR-020).
    /// Also throws <see cref="DuplicateFolderNoteException"/> (another live note has the folder) and
    /// <see cref="KeyNotFoundException"/> (missing or deleted).
    /// </summary>
    Task<Note> RelinkAsync(Guid id, FolderRef newFolder, FolderRef? expectedFolder, CancellationToken ct = default);

    /// <summary>
    /// Sets or clears <see cref="Note.OrphanedAt"/>. False when it already was so (or the note is
    /// deleted). <paramref name="expectedFolder"/> works as in <see cref="RelinkAsync"/>.
    /// </summary>
    Task<bool> SetOrphanedAsync(Guid id, bool orphaned, FolderRef? expectedFolder, CancellationToken ct = default);

    event EventHandler<NoteChangedEventArgs>? NoteChanged;
}

public interface ILabelRepository
{
    /// <summary>All labels, sorted by name.</summary>
    Task<IReadOnlyList<Label>> ListLabelsAsync(CancellationToken ct = default);

    Task<Label> CreateLabelAsync(string name, string color, CancellationToken ct = default);

    /// <summary>Rename and/or recolor (REQ-012). Every note shows the new name.</summary>
    Task<Label> UpdateLabelAsync(Label label, CancellationToken ct = default);

    /// <summary>Deletes the label and removes it from every note.</summary>
    Task DeleteLabelAsync(Guid id, CancellationToken ct = default);

    /// <summary>Live notes (orphaned included) per label id. Unused labels are absent (03, T2.5).</summary>
    Task<IReadOnlyDictionary<Guid, int>> CountNotesByLabelAsync(CancellationToken ct = default);

    event EventHandler? LabelsChanged;
}
