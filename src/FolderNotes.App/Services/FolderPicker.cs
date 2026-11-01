using Microsoft.UI;

namespace FolderNotes.App.Services;

/// <summary>Lets the user pick a folder (Relink…, 05 § Orphaned page). Abstracted for view-model tests.</summary>
internal interface IFolderPicker
{
    /// <summary>The picked folder's path, or null when the user cancelled.</summary>
    Task<string?> PickFolderAsync(string? startFolder, string commitText);
}

/// <summary>
/// The Windows App SDK folder picker (returns a plain path; no StorageFolder or broker),
/// owned by a window so it's modal to it.
/// </summary>
internal sealed class WindowFolderPicker(Func<WindowId> owner) : IFolderPicker
{
    private readonly Func<WindowId> _owner = owner ?? throw new ArgumentNullException(nameof(owner));

    public async Task<string?> PickFolderAsync(string? startFolder, string commitText)
    {
        var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(_owner()) { CommitButtonText = commitText };
        if (!string.IsNullOrEmpty(startFolder))
        {
            picker.SuggestedStartFolder = startFolder;
        }

        var result = await picker.PickSingleFolderAsync();
        return result?.Path;
    }
}
