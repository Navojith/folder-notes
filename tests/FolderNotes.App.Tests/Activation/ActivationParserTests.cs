using FolderNotes.App.Activation;
using static FolderNotes.App.Activation.ActivationCommand;

namespace FolderNotes.App.Tests.Activation;

/// <summary>specs/02 § Command-line / activation contract ("Resolved in T2.1").</summary>
public sealed class ActivationParserTests
{
    private const string Exe = @"C:\Program Files\Folder Notes\FolderNotes.exe";

    [Fact]
    public void REQ080_NoArgs_ShowsMainWindow() =>
        ActivationParser.Parse([]).ShouldBe(new ShowMain());

    [Fact]
    public void REQ080_Background_IsTrayOnly() =>
        ActivationParser.Parse(["--background"]).ShouldBe(new Background());

    [Theory]
    [InlineData("--edit")]
    [InlineData("--EDIT")]
    [InlineData("/edit")]
    [InlineData("/Edit")]
    public void REQ003_Edit_AcceptsSwitchSpellings(string flag) =>
        ActivationParser.Parse([flag, @"C:\Temp\x"]).ShouldBe(new Edit(@"C:\Temp\x"));

    [Fact]
    public void REQ003_Edit_KeepsPathWithSpacesAndUnicode() =>
        ActivationParser.Parse(["--edit", @"D:\Clients\Acme Ltd\Ünïcode 📁"]).ShouldBe(new Edit(@"D:\Clients\Acme Ltd\Ünïcode 📁"));

    [Theory]
    [InlineData]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("--background")]
    public void REQ003_Edit_WithoutPath_IsInvalid(params string[] rest) =>
        ActivationParser.Parse(["--edit", .. rest]).ShouldBeOfType<Invalid>();

    [Fact]
    public void REQ003_Edit_IgnoresTokensAfterThePath() =>
        ActivationParser.Parse(["--edit", @"C:\A", "--search", "x"]).ShouldBe(new Edit(@"C:\A"));

    [Fact]
    public void REQ050_Search_WithoutQuery_IsEmpty() =>
        ActivationParser.Parse(["--search"]).ShouldBe(new ActivationCommand.Search(string.Empty));

    [Fact]
    public void REQ050_Search_JoinsRemainingTokens() =>
        ActivationParser.Parse(["--search", "acme", "label:client"]).ShouldBe(new ActivationCommand.Search("acme label:client"));

    [Fact]
    public void REQ080_OpenNote_ParsesGuid()
    {
        var id = Guid.NewGuid();
        ActivationParser.Parse(["--open-note", id.ToString("B")]).ShouldBe(new OpenNote(id));
    }

    [Theory]
    [InlineData]
    [InlineData("not-a-guid")]
    public void REQ080_OpenNote_WithoutGuid_IsInvalid(params string[] rest) =>
        ActivationParser.Parse(["--open-note", .. rest]).ShouldBeOfType<Invalid>();

    [Theory]
    [InlineData("--frobnicate")]
    [InlineData(@"C:\Temp\x")]
    [InlineData("--")]
    [InlineData("-edit")]
    public void REQ080_UnknownOrStrayTokens_AreInvalidWithReason(string token)
    {
        var result = ActivationParser.Parse([token]).ShouldBeOfType<Invalid>();
        result.Reason.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void REQ080_Background_WithStrayValue_IsInvalid() =>
        ActivationParser.Parse(["--background", "now"]).ShouldBeOfType<Invalid>();

    [Fact]
    public void REQ080_CommandLine_DropsQuotedExecutable() =>
        ActivationParser.ParseCommandLine($"\"{Exe}\" --edit \"C:\\Temp\\x y\"", Exe).ShouldBe(new Edit(@"C:\Temp\x y"));

    [Fact]
    public void REQ080_CommandLine_DropsExecutableByFileNameOnly() =>
        ActivationParser.ParseCommandLine(@"foldernotes.EXE --search acme", Exe).ShouldBe(new ActivationCommand.Search("acme"));

    [Fact]
    public void REQ080_CommandLine_WithoutExecutable_StillParses() =>
        ActivationParser.ParseCommandLine("--edit \"C:\\Temp\\x y\"", Exe).ShouldBe(new Edit(@"C:\Temp\x y"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData($"\"{Exe}\"")]
    public void REQ080_CommandLine_Empty_ShowsMainWindow(string? commandLine) =>
        ActivationParser.ParseCommandLine(commandLine, Exe).ShouldBe(new ShowMain());

    [Fact]
    public void REQ080_Split_FollowsWindowsQuotingRules() =>
        ActivationParser.SplitCommandLine("a \"b c\" \"d\\\"e\" C:\\Trailing\\").ShouldBe(["a", "b c", "d\"e", @"C:\Trailing\"]);

    [Theory]
    [InlineData("--edit \"D:\\\"", @"D:\")]
    [InlineData("--edit \"\\\\server\\share\\\"", @"\\server\share\")]
    [InlineData("\"C:\\x\\FolderNotes.exe\" --edit \"C:\\\" ", @"C:\")] // as AppInstance forwards it: trailing space
    public void REQ003_CommandLine_QuotedPathWithTrailingBackslash_IsRepaired(string commandLine, string expected) =>
        // Explorer passes "%V"; for a drive root that is "D:\" and Windows reads \" as a literal quote.
        ActivationParser.ParseCommandLine(commandLine, Exe).ShouldBe(new Edit(expected));
}
