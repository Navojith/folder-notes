using System.Text.RegularExpressions;
using FolderNotes.App.Tests.Themes;

namespace FolderNotes.App.Tests.Explorer;

/// <summary>
/// T3.2.2 boundary (04 B.1, ADR-007): the pure tracker logic under <c>Explorer/</c> references no COM, Win32,
/// reflection-based binding or UI. When T3.2.3 adds the COM layer it goes in its own subfolder, which these
/// rules do not cover; the logic files listed here must stay clean.
/// </summary>
public class ExplorerLogicSourceGuardTests
{
    private static readonly string[] LogicFiles =
    [
        "ActiveFolderState.cs", "ComFailureClassifier.cs", "TabMap.cs", "EvaluationScheduler.cs",
        "StatePublisher.cs", "EvaluationWatchdog.cs", "TrackerStateMachine.cs",
    ];

    private static readonly (string Name, Regex Pattern)[] Banned =
    [
        ("System.Runtime.InteropServices", new Regex(@"System\.Runtime\.InteropServices")),
        ("P/Invoke attribute", new Regex(@"\b(DllImport|LibraryImport|GeneratedComInterface|GeneratedComClass|ComImport)\b")),
        ("Marshal class", new Regex(@"\bMarshal\.")),
        ("dynamic", new Regex(@"\bdynamic\b")),
        ("InvokeMember/reflection binding", new Regex(@"\b(InvokeMember|GetTypeFromProgID|Activator\.CreateInstance)\b")),
        ("UI types", new Regex(@"\bMicrosoft\.UI\b|\bDispatcherQueue\b|\bWindows\.")),
        ("IntPtr", new Regex(@"\bIntPtr\b")),
    ];

    [Fact]
    public void ADR007_The_pure_logic_files_use_no_COM_Win32_dynamic_or_UI_types()
    {
        var dir = Path.Combine(RepoFiles.AppDir, "Explorer");
        foreach (var name in LogicFiles)
        {
            var path = Path.Combine(dir, name);
            File.Exists(path).ShouldBeTrue(path);
            var text = StripComments(File.ReadAllText(path));
            foreach (var (what, pattern) in Banned)
            {
                pattern.IsMatch(text).ShouldBeFalse($"{name} must not use {what} (pure logic, T3.2.2)");
            }
        }
    }

    [Fact]
    public void ADR007_No_dynamic_or_InvokeMember_anywhere_under_Explorer()
    {
        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoFiles.AppDir, "Explorer"), "*.cs", SearchOption.AllDirectories))
        {
            var text = StripComments(File.ReadAllText(file));
            Regex.IsMatch(text, @"\bdynamic\b|\bInvokeMember\b").ShouldBeFalse($"{Path.GetFileName(file)}: late-bound COM is banned (ADR-007)");
        }
    }

    [Fact]
    public void T322_Nothing_starts_the_tracker_yet_AppShell_and_App_do_not_reference_it()
    {
        foreach (var name in new[] { "App.xaml.cs", "AppShell.cs" })
        {
            File.ReadAllText(Path.Combine(RepoFiles.AppDir, name)).ShouldNotContain("FolderNotes.App.Explorer");
        }
    }

    private static string StripComments(string source) =>
        Regex.Replace(Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline), @"//[^\n]*", "");
}
