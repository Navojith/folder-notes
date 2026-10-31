namespace FolderNotes.Core.Domain;

/// <summary>Note status (REQ-002). Stored as its kebab-case key, see <see cref="NoteStatusExtensions"/>.</summary>
public enum NoteStatus
{
    None,
    Keep,
    SafeToDelete,
    Archive,
    InProgress,
    Review,
}

public static class NoteStatusExtensions
{
    private static readonly string[] s_keys = ["none", "keep", "safe-to-delete", "archive", "in-progress", "review"];

    /// <summary>All storage keys, in enum order.</summary>
    public static IReadOnlyList<string> Keys => s_keys;

    public static bool IsDefined(this NoteStatus status) => (int)status >= 0 && (int)status < s_keys.Length;

    /// <summary>The storage / search key, e.g. <c>safe-to-delete</c>.</summary>
    public static string ToKey(this NoteStatus status) =>
        status.IsDefined()
            ? s_keys[(int)status]
            : throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown status.");

    public static bool TryParseKey(string? key, out NoteStatus status)
    {
        var trimmed = key?.Trim();
        var i = Array.FindIndex(s_keys, k => string.Equals(k, trimmed, StringComparison.OrdinalIgnoreCase));
        status = i < 0 ? NoteStatus.None : (NoteStatus)i;
        return i >= 0;
    }

    public static NoteStatus ParseKey(string key) =>
        TryParseKey(key, out var s) ? s : throw new FormatException($"Unknown note status '{key}'.");
}
