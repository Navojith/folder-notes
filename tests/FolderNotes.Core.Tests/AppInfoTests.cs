using FolderNotes.Core;

namespace FolderNotes.Core.Tests;

public sealed class AppInfoTests
{
    [Fact]
    public void NFR03_DataDirectory_IsUnderLocalAppData()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        AppInfo.DataDirectory.ShouldStartWith(local);
        AppInfo.DataDirectory.ShouldEndWith(AppInfo.DataFolderName);
    }
}
