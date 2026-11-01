using System.Diagnostics;

namespace FolderNotes.App.Services;

/// <summary>Opens a folder in Explorer (editor header, library row menu).</summary>
internal static class FolderLauncher
{
    /// <summary>False when the folder doesn't exist (nothing is started).</summary>
    public static bool Open(string? path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
        {
            return false;
        }

        // explorer.exe does its own argument parsing; a quoted drive root ("D:\") is fine there.
        using var _ = Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = false });
        return true;
    }
}
