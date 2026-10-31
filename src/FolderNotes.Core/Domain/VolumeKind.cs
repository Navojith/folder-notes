namespace FolderNotes.Core.Domain;

/// <summary>Kind of volume a folder lives on (specs/03-data-model.md, REQ-062).</summary>
public enum VolumeKind
{
    /// <summary>Local fixed disk: rename/move tracking by file ID.</summary>
    Fixed,

    /// <summary>Removable drive: tracked by path only.</summary>
    Removable,

    /// <summary>Network share: tracked by path only.</summary>
    Network,
}

public static class VolumeKindExtensions
{
    public static string ToKey(this VolumeKind kind) => kind switch
    {
        VolumeKind.Fixed => "fixed",
        VolumeKind.Removable => "removable",
        VolumeKind.Network => "network",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown volume kind."),
    };

    public static VolumeKind ParseVolumeKind(string key) => key switch
    {
        "fixed" => VolumeKind.Fixed,
        "removable" => VolumeKind.Removable,
        "network" => VolumeKind.Network,
        _ => throw new FormatException($"Unknown volume kind '{key}'."),
    };
}
