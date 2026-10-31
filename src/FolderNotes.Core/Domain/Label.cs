namespace FolderNotes.Core.Domain;

/// <summary>A global label (REQ-010). Names are unique, case-insensitively.</summary>
public sealed record Label(Guid Id, string Name, string Color)
{
    public const int MaxNameLength = 40;

    /// <summary>How label names are compared for uniqueness (REQ-010).</summary>
    public static StringComparer NameComparer { get; } = StringComparer.OrdinalIgnoreCase;

    public static Label Create(string name, string color) => new(Guid.NewGuid(), name.Trim(), color);

    /// <summary>Built-in starter labels (REQ-013). All of them are editable.</summary>
    public static IReadOnlyList<(string Name, string Color)> StarterLabels { get; } =
    [
        ("client", "blue"),
        ("build-output", "amber"),
        ("archive", "indigo"),
        ("personal", "green"),
        ("do-not-delete", "red"),
    ];
}
