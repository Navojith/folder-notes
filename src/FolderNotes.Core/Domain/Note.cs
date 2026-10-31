namespace FolderNotes.Core.Domain;

/// <summary>A folder note (REQ-001, REQ-002, REQ-020). Immutable, so edit it with <c>with</c>.</summary>
public sealed record Note
{
    public const int MaxTitleLength = 120;
    public const int MaxBodyLength = 50_000;

    public required Guid Id { get; init; }
    public required FolderRef Folder { get; init; }
    public required string Title { get; init; }
    public string BodyMd { get; init; } = "";
    public IReadOnlyList<Label> Labels { get; init; } = [];
    public NoteStatus Status { get; init; } = NoteStatus.None;

    /// <summary>Optional palette key (<see cref="Palette"/>).</summary>
    public string? AccentColor { get; init; }

    public DateOnly? KeepUntil { get; init; }
    public DateOnly? ReviewOn { get; init; }
    public DateOnly? SafeAfter { get; init; }

    /// <summary>REQ-071 opt-in sidecar.</summary>
    public bool TravelWithFolder { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? DeletedAt { get; init; }
    public DateTimeOffset? OrphanedAt { get; init; }

    public bool IsDeleted => DeletedAt is not null;
    public bool IsOrphaned => OrphanedAt is not null;

    /// <summary>
    /// The next reminder date: the earlier of <see cref="ReviewOn"/> and <see cref="SafeAfter"/>.
    /// Used by the <c>due:</c> filters and the "due date" sort.
    /// </summary>
    public DateOnly? DueDate => (ReviewOn, SafeAfter) switch
    {
        ({ } r, { } s) => r < s ? r : s,
        ({ } r, null) => r,
        (null, { } s) => s,
        _ => null,
    };

    /// <summary>A new note for a folder, with the folder name as the suggested title (REQ-003).</summary>
    public static Note CreateFor(FolderRef folder, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(folder);
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        var title = TextLength.Truncate(folder.FolderName, MaxTitleLength);
        return new Note { Id = Guid.NewGuid(), Folder = folder, Title = title, CreatedAt = now, UpdatedAt = now };
    }
}
