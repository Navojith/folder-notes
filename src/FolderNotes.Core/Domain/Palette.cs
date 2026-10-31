namespace FolderNotes.Core.Domain;

/// <summary>The fixed 12-color palette for labels and note accents (REQ-010, specs/05-ui-design.md).</summary>
public static class Palette
{
    public static IReadOnlyList<string> Keys { get; } =
        ["red", "orange", "amber", "yellow", "lime", "green", "teal", "cyan", "blue", "indigo", "purple", "pink"];

    public static bool IsValid(string? key) => key is not null && Keys.Contains(key, StringComparer.Ordinal);
}
