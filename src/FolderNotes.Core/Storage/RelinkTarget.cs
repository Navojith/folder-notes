using FolderNotes.Core.Domain;
using FolderNotes.Core.Identity;

namespace FolderNotes.Core.Storage;

public enum RelinkProblem
{
    None,

    /// <summary>Empty, invalid, missing, or a file.</summary>
    NotAFolder,

    /// <summary>The picked folder is in the Recycle Bin.</summary>
    InRecycleBin,
}

/// <param name="Target">The folder to relink to, when <paramref name="Problem"/> is <see cref="RelinkProblem.None"/>.</param>
/// <param name="Problem">Why the folder can't be used.</param>
/// <param name="NameDiffers">The folder's name isn't the note's current folder name (ignoring case): the app asks first.</param>
/// <param name="SameFolder">It's the folder the note is already bound to.</param>
public sealed record RelinkTargetResult(FolderRef? Target, RelinkProblem Problem, bool NameDiffers = false, bool SameFolder = false);

/// <summary>Checks a folder the user picked for <c>Relink…</c> before anything is written (03 § Relink rules).</summary>
public static class RelinkTarget
{
    public static RelinkTargetResult Check(IFolderIdentityService identity, Note note, string? pickedPath, Func<string, bool>? directoryExists = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(note);
        var exists = directoryExists ?? Directory.Exists;

        if (string.IsNullOrWhiteSpace(pickedPath) || !identity.TryResolve(pickedPath, out var folder) || !exists(folder.PathDisplay))
        {
            return new(null, RelinkProblem.NotAFolder);
        }

        if (FolderIdentityService.IsInRecycleBin(folder.PathDisplay))
        {
            return new(null, RelinkProblem.InRecycleBin);
        }

        var nameDiffers = !string.Equals(folder.FolderName, note.Folder.FolderName, StringComparison.OrdinalIgnoreCase);
        return new(folder, RelinkProblem.None, nameDiffers, folder.SameBindingAs(note.Folder));
    }
}
