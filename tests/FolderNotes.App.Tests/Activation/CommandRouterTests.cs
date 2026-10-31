using FolderNotes.App.Activation;
using Serilog.Core;
using static FolderNotes.App.Activation.ActivationCommand;

namespace FolderNotes.App.Tests.Activation;

/// <summary>Single-instance arg routing (specs/06 § Layers "App services", specs/02).</summary>
public sealed class CommandRouterTests
{
    private readonly FakeShell _shell = new();
    private readonly CommandRouter _router;

    public CommandRouterTests() => _router = new CommandRouter(_shell, Logger.None);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void REQ080_ShowMain_OpensMainWindow(bool redirected)
    {
        _router.Route(new ShowMain(), Source(redirected));
        _shell.Calls.ShouldBe(["main"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void REQ080_Background_OpensNothing(bool redirected)
    {
        _router.Route(new Background(), Source(redirected));
        _shell.Calls.ShouldBeEmpty();
    }

    [Fact]
    public void REQ003_Edit_OpensEditorForThatFolder()
    {
        _router.Route(new Edit(@"C:\Temp\x"), ActivationSource.Redirected);
        _shell.Calls.ShouldBe([@"edit C:\Temp\x"]);
    }

    [Fact]
    public void REQ050_Search_OpensSearchWithQuery()
    {
        _router.Route(new Search("acme"), ActivationSource.Tray);
        _shell.Calls.ShouldBe(["search acme"]);
    }

    [Fact]
    public void REQ080_OpenNote_OpensThatNote()
    {
        var id = Guid.NewGuid();
        _router.Route(new OpenNote(id), ActivationSource.Redirected);
        _shell.Calls.ShouldBe([$"note {id}"]);
    }

    [Fact]
    public void REQ080_Invalid_FallsBackToMainWindow()
    {
        _router.Route(new Invalid("bad"), ActivationSource.Redirected);
        _shell.Calls.ShouldBe(["main"]);
    }

    [Fact]
    public void REQ080_RedirectedCommandLine_EndToEnd()
    {
        // What the main instance does with a forwarded launch string.
        _router.Route(ActivationParser.ParseCommandLine("\"C:\\x\\FolderNotes.exe\" --edit \"D:\\Clients\\Acme Ltd\"", @"C:\x\FolderNotes.exe"),
            ActivationSource.Redirected);
        _shell.Calls.ShouldBe([@"edit D:\Clients\Acme Ltd"]);
    }

    private static ActivationSource Source(bool redirected) =>
        redirected ? ActivationSource.Redirected : ActivationSource.Startup;

    private sealed class FakeShell : IAppShell
    {
        public List<string> Calls { get; } = [];

        public void ShowMainWindow() => Calls.Add("main");

        public void OpenEditor(string folderPath) => Calls.Add($"edit {folderPath}");

        public void OpenSearch(string query) => Calls.Add($"search {query}");

        public void OpenNote(Guid noteId) => Calls.Add($"note {noteId}");
    }
}
