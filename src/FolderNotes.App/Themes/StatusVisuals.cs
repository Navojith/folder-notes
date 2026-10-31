using FolderNotes.Core.Domain;

namespace FolderNotes.App.Themes;

/// <summary>How a status looks: icon, tint and text (specs/05 § Visual language, § Design tokens).</summary>
/// <param name="Status">The status.</param>
/// <param name="Text">Display text, also used for the UIA name ("Status: Keep").</param>
/// <param name="IconKey">Resource key of the icon geometry string in Tokens.xaml, or null for <see cref="NoteStatus.None"/>.</param>
/// <param name="TintKey">Palette key of the tint (<see cref="Palette.Keys"/>), or null for <see cref="NoteStatus.None"/>.</param>
internal sealed record StatusVisual(NoteStatus Status, string Text, string? IconKey, string? TintKey)
{
    public bool HasPill => IconKey is not null;
}

internal static class StatusVisuals
{
    private static readonly StatusVisual[] s_all =
    [
        new(NoteStatus.None, "No status", null, null),
        new(NoteStatus.Keep, "Keep", Icon(NoteStatus.Keep), "blue"),
        new(NoteStatus.SafeToDelete, "Safe to delete", Icon(NoteStatus.SafeToDelete), "green"),
        new(NoteStatus.Archive, "Archive", Icon(NoteStatus.Archive), "indigo"),
        new(NoteStatus.InProgress, "In progress", Icon(NoteStatus.InProgress), "amber"),
        new(NoteStatus.Review, "Review", Icon(NoteStatus.Review), "purple"),
    ];

    /// <summary>Every status, in enum order.</summary>
    public static IReadOnlyList<StatusVisual> All => s_all;

    public static StatusVisual For(NoteStatus status) =>
        status.IsDefined()
            ? s_all[(int)status]
            : throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown status.");

    private static string Icon(NoteStatus status) => "StatusIcon." + status.ToKey();
}
