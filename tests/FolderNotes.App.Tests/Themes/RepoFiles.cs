namespace FolderNotes.App.Tests.Themes;

/// <summary>Finds source files: these tests check the checked-in XAML/C#, not build output.</summary>
internal static class RepoFiles
{
    public static string Root { get; } = FindRoot();

    public static string AppDir => Path.Combine(Root, "src", "FolderNotes.App");

    public static string TokensPath => Path.Combine(AppDir, "Themes", "Tokens.xaml");

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "FolderNotes.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Repository root (FolderNotes.slnx) not found above " + AppContext.BaseDirectory);
    }
}
