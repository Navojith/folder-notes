using FolderNotes.App.Services;
using Microsoft.Win32;

namespace FolderNotes.App.Tests.Settings;

/// <summary>`RegistryShellVerbRegistrar` (04 § 4.1, ADR-024) against a scratch HKCU root, never the real Classes keys.</summary>
public sealed class ShellVerbRegistrarTests : IDisposable
{
    private const string Exe = @"C:\Apps\Folder Notes ünï\FolderNotes.exe";
    private readonly string _root = @"Software\FolderNotesTestsShell\Classes-" + Guid.NewGuid().ToString("N")[..8];

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(_root, throwOnMissingSubKey: false);

    private RegistryShellVerbRegistrar Create(string exe = Exe) => new(exe, _root);

    private string? Read(string subPath, string? name = null)
    {
        using var k = Registry.CurrentUser.OpenSubKey(_root + @"\" + subPath);
        return k?.GetValue(name) as string;
    }

    [Fact]
    public void REQ003_Enable_WritesBothVerbs_WithQuotedPaths()
    {
        var sut = Create();
        sut.GetState().ShouldBe(ShellVerbState.Off);

        sut.Enable();

        sut.GetState().ShouldBe(ShellVerbState.On);
        foreach (var parent in new[] { @"Directory\shell", @"Directory\Background\shell" })
        {
            Read(parent + @"\FolderNotes").ShouldBe("Folder note…");
            Read(parent + @"\FolderNotes", "Icon").ShouldBe("\"" + Exe + "\",0");
            Read(parent + @"\FolderNotes\command").ShouldBe("\"" + Exe + "\" --edit \"%V\"");
        }
    }

    [Fact]
    public void REQ004_Enable_IsIdempotent_AndDisableRemovesOnlyOurKeys()
    {
        var sut = Create();
        using (var k = Registry.CurrentUser.CreateSubKey(_root + @"\Directory\shell\SomeoneElse\command"))
        {
            k.SetValue(null, "other.exe");
        }

        sut.Enable();
        sut.Enable();
        sut.GetState().ShouldBe(ShellVerbState.On);

        sut.Disable();
        sut.Disable(); // nothing left: no error

        sut.GetState().ShouldBe(ShellVerbState.Off);
        Read(@"Directory\shell\SomeoneElse\command").ShouldBe("other.exe");
    }

    [Fact]
    public void ADR024_AnotherExe_IsStale_AndEnableRepairsIt()
    {
        Create(@"C:\Old\FolderNotes.exe").Enable();
        var sut = Create();

        sut.GetState().ShouldBe(ShellVerbState.Stale);
        sut.RegisteredExe().ShouldBe(@"C:\Old\FolderNotes.exe");

        sut.Enable();
        sut.GetState().ShouldBe(ShellVerbState.On);
        sut.RegisteredExe().ShouldBe(Exe);
    }

    [Fact]
    public void ADR024_OneKeyMissing_Or_NoCommand_IsStale()
    {
        var sut = Create();
        sut.Enable();
        using (var shell = Registry.CurrentUser.OpenSubKey(_root + @"\Directory\Background\shell", writable: true))
        {
            shell!.DeleteSubKeyTree("FolderNotes");
        }

        sut.GetState().ShouldBe(ShellVerbState.Stale);

        sut.Enable();
        using (var key = Registry.CurrentUser.OpenSubKey(_root + @"\Directory\shell\FolderNotes", writable: true))
        {
            key!.DeleteSubKeyTree("command");
        }

        sut.GetState().ShouldBe(ShellVerbState.Stale);
        sut.Disable();
        sut.GetState().ShouldBe(ShellVerbState.Off);
    }

    [Fact]
    public void ADR024_DisableWithNothingRegistered_DoesNotCreateKeys()
    {
        Create().Disable();
        Registry.CurrentUser.OpenSubKey(_root).ShouldBeNull();
    }

    [Fact]
    public void ADR024_CliSwitches_RegisterAndUnregister_AndIgnoreEverythingElse()
    {
        var sut = Create();

        ShellVerbCommand.TryHandle(["--register-shell"], sut, out var code).ShouldBeTrue();
        code.ShouldBe(0);
        sut.GetState().ShouldBe(ShellVerbState.On);

        ShellVerbCommand.TryHandle(["--unregister-shell"], sut, out code).ShouldBeTrue();
        code.ShouldBe(0);
        sut.GetState().ShouldBe(ShellVerbState.Off);

        ShellVerbCommand.TryHandle(["--edit", @"C:\x"], sut, out _).ShouldBeFalse();
        ShellVerbCommand.TryHandle([], sut, out _).ShouldBeFalse();
        ShellVerbCommand.TryHandle(["--background"], sut, out _).ShouldBeFalse();
        sut.GetState().ShouldBe(ShellVerbState.Off);
    }

    [Fact]
    public void ADR024_CliSwitch_ReportsFailureAsExitCode1()
    {
        ShellVerbCommand.TryHandle(["--register-shell"], new ThrowingRegistrar(), out var code).ShouldBeTrue();
        code.ShouldBe(1);
    }

    [Fact]
    public void ADR024_TheAppNeverRegistersTheVerbsByItself()
    {
        // App start-up only hands the registrar to Settings; nothing there or in the shell enables or disables it.
        foreach (var name in new[] { "App.xaml.cs", "AppShell.cs" })
        {
            var text = File.ReadAllText(Path.Combine(Themes.RepoFiles.AppDir, name));
            text.ShouldNotContain(".Enable()");
            text.ShouldNotContain("SetShellVerbs");
            text.ShouldNotContain("TryHandle(");
        }
    }

    private sealed class ThrowingRegistrar : IShellVerbRegistrar
    {
        public ShellVerbState GetState() => ShellVerbState.Off;
        public void Enable() => throw new UnauthorizedAccessException("denied");
        public void Disable() => throw new UnauthorizedAccessException("denied");
        public string? RegisteredExe() => null;
    }
}
