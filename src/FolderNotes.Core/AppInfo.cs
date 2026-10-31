namespace FolderNotes.Core;

/// <summary>Product-wide constants shared by the app and (by convention) the shell extension.</summary>
public static class AppInfo
{
    public const string ProductName = "Folder Notes";

    /// <summary>Data folder name under %LOCALAPPDATA% (NFR-03).</summary>
    public const string DataFolderName = "FolderNotes";

    /// <summary>Database file name inside the data folder (specs/03-data-model.md).</summary>
    public const string DatabaseFileName = "notes.db";

    /// <summary>Full path of the data folder for the current user.</summary>
    public static string DataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), DataFolderName);
}
