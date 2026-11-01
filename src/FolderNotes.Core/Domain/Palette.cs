namespace FolderNotes.Core.Domain;

/// <summary>The fixed 12-color palette for labels and note accents (REQ-010, specs/05-ui-design.md).</summary>
public static class Palette
{
    public static IReadOnlyList<string> Keys { get; } =
        ["red", "orange", "amber", "yellow", "lime", "green", "teal", "cyan", "blue", "indigo", "purple", "pink"];

    public static bool IsValid(string? key) => key is not null && Keys.Contains(key, StringComparer.Ordinal);

    /// <summary>
    /// The palette colour used the fewest times in <paramref name="usedColors"/>; ties go to palette
    /// order (specs/05: a new label's default colour).
    /// </summary>
    public static string LeastUsed(IEnumerable<string> usedColors)
    {
        ArgumentNullException.ThrowIfNull(usedColors);
        var counts = usedColors.GroupBy(c => c, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        return Keys.OrderBy(k => counts.GetValueOrDefault(k)).First();
    }
}
