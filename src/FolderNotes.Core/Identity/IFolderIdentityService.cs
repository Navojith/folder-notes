using System.Diagnostics.CodeAnalysis;
using FolderNotes.Core.Domain;

namespace FolderNotes.Core.Identity;

public enum LocateStatus
{
    /// <summary>The folder exists. <see cref="LocateResult.Path"/> is where it is now (it may have moved).</summary>
    Found,

    /// <summary>The folder still exists, but in the Recycle Bin (S4). Treat it as orphaned, don't relink.</summary>
    InRecycleBin,

    /// <summary>The folder is gone (REQ-061 orphan).</summary>
    NotFound,
}

/// <summary>Where a stored folder is now. <see cref="Path"/> is a display path when the status isn't NotFound.</summary>
public sealed record LocateResult(LocateStatus Status, string? Path)
{
    public static LocateResult NotFound { get; } = new(LocateStatus.NotFound, null);
}

/// <summary>Folder identity (REQ-060…063, specs/02-architecture.md, specs/03-data-model.md).</summary>
public interface IFolderIdentityService
{
    /// <summary>Path → canonical, normalized path plus volume/file ID. Throws <see cref="ArgumentException"/> for invalid paths or files.</summary>
    FolderRef Resolve(string path);

    bool TryResolve(string path, [NotNullWhen(true)] out FolderRef? folder);

    /// <summary>Finds the current location of a stored folder, by file ID first.</summary>
    LocateResult Locate(FolderRef storedRef);
}
