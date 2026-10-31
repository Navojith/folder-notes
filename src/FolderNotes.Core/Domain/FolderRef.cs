namespace FolderNotes.Core.Domain;

/// <summary>
/// Identifies a folder (specs/03-data-model.md § Folder identity). Built by
/// <c>IFolderIdentityService.Resolve</c>. Tests may build it directly.
/// </summary>
/// <param name="PathNorm">Normalized compare key (REQ-063).</param>
/// <param name="PathDisplay">Path as the user sees it, original casing.</param>
/// <param name="VolumeSerial">Hex volume serial, or null for network/removable/unknown.</param>
/// <param name="FileId">Hex 128-bit <c>FILE_ID_INFO.FileId</c>, or null when not tracked.</param>
/// <param name="VolumeKind">Fixed, removable or network.</param>
public sealed record FolderRef(
    string PathNorm,
    string PathDisplay,
    string? VolumeSerial,
    string? FileId,
    VolumeKind VolumeKind = VolumeKind.Fixed)
{
    /// <summary>True when the folder can be tracked across rename/move by file ID (REQ-060).</summary>
    public bool HasFileId => VolumeSerial is not null && FileId is not null;

    /// <summary>The last path segment, used as the suggested title (REQ-003).</summary>
    public string FolderName
    {
        get
        {
            var trimmed = PathDisplay.TrimEnd('\\', '/');
            var i = trimmed.LastIndexOfAny(['\\', '/']);
            var name = i < 0 ? trimmed : trimmed[(i + 1)..];
            return name.Length == 0 ? PathDisplay : name;
        }
    }
}
