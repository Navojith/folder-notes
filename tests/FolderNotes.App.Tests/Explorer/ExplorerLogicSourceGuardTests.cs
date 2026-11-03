using System.Text.RegularExpressions;
using FolderNotes.App.Tests.Themes;

namespace FolderNotes.App.Tests.Explorer;

/// <summary>
/// T3.2.2 boundary (04 B.1, ADR-007): the pure tracker logic under <c>Explorer/</c> references no COM, Win32,
/// reflection-based binding or UI. When T3.2.3 adds the COM layer it goes in its own subfolder, which these
/// rules do not cover; the logic files listed here must stay clean.
/// </summary>
[Trait("Kind", "Deterministic")]
public class ExplorerLogicSourceGuardTests
{
    private static readonly string[] LogicFiles =
    [
        "ActiveFolderState.cs", "ComFailureClassifier.cs", "TabMap.cs", "EvaluationScheduler.cs",
        "StatePublisher.cs", "EvaluationWatchdog.cs", "TrackerStateMachine.cs", "SignalRouter.cs",
        "CallJournal.cs", "Quarantine.cs",
    ];

    /// <summary>04 B.7 / J-H11: the hook thread and everything it calls are COM-free, file-system-free and UI-free.</summary>
    private static readonly string[] HookLayerFiles = [Path.Combine("Native", "HookThread.cs"), Path.Combine("Native", "HookNative.cs"), "SignalRouter.cs"];

    private static readonly (string Name, Regex Pattern)[] BannedInHookLayer =
    [
        ("COM references", new Regex(@"\b(ComRef|ComObject|GeneratedComInterface|ComImport|CoInitialize\w*|CoCreateInstance|IShellWindows\w*|IShellWindowsGateway|IWebBrowser2)\b")),
        ("Marshal class", new Regex(@"\bMarshal\.")),
        ("UI dispatching", new Regex(@"\b(IUiDispatcher|DispatcherQueue|Microsoft\.UI|Windows\.UI)\b")),
        ("file system", new Regex(@"\b(System\.IO|File\.|Directory\.|Path\.|FileInfo|DirectoryInfo)\b")),
        ("SQLite / notes", new Regex(@"\b(Sqlite|INoteStore|IFolderIdentityService)\w*")),
    ];

    /// <summary>04 B.6 (3), B.7: every engine gets the generation-gated scope channel, never the raw hook thread (a zombie must not re-scope hooks).</summary>
    [Fact]
    public void NFR01_Engines_are_given_a_generation_gated_hook_scope()
    {
        var text = StripComments(File.ReadAllText(Path.Combine(RepoFiles.AppDir, "Explorer", "ExplorerTracker.cs")));
        Regex.IsMatch(text, @"new TrackerEngine\([^;]*new GenerationScope\(generation,").ShouldBeTrue();
        Regex.IsMatch(text, @"new TrackerEngine\([^;]*,\s*_hooks\s*,").ShouldBeFalse();
    }

    /// <summary>04 L: the engine runs on the worker the host gives it; it creates no thread, task, timer or Win32/COM call of its own.</summary>
    [Fact]
    public void NFR01_The_engine_owns_no_thread_timer_or_interop()
    {
        var text = StripComments(File.ReadAllText(Path.Combine(RepoFiles.AppDir, "Explorer", "TrackerEngine.cs")));
        Regex.IsMatch(text, @"\b(new Thread|Task\.Run|ThreadPool|new Timer|new System\.Threading\.Timer|LibraryImport|DllImport|Marshal\.|GeneratedComInterface|ComRef<)").ShouldBeFalse();
    }

    /// <summary>04 B.7(c): the watchdog calls CoCancelCall with a thread id and reports a wedge; it touches no hook and no COM object.</summary>
    [Fact]
    public void NFR01_The_watchdog_thread_touches_neither_hooks_nor_COM_objects()
    {
        var text = StripComments(File.ReadAllText(Path.Combine(RepoFiles.AppDir, "Explorer", "Native", "WatchdogThread.cs")));
        Regex.IsMatch(text, @"\b(HookThread|IWinEventApi|SetHook|Unhook|ComRef|ComObject|IShellWindows\w*|IShellBrowser|Marshal\.)\b").ShouldBeFalse();
    }

    /// <summary>04 B.2: only the COM worker host and the COM folder may create COM objects; nothing outside them references ComRef or the interfaces.</summary>
    [Fact]
    public void NFR01_COM_types_are_referenced_only_inside_the_Com_folder_and_the_worker_host()
    {
        var dir = Path.Combine(RepoFiles.AppDir, "Explorer");
        foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(dir, file);
            if (rel.StartsWith("Com" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                continue;
            }

            var text = StripComments(File.ReadAllText(file));
            Regex.IsMatch(text, @"\b(ComRef<|GeneratedComInterface|IShellWindows\b|IShellBrowser|IWebBrowser2|IFolderView|IPersistFolder2)").ShouldBeFalse($"{rel} must not touch COM types (04 B.2)");
        }
    }

    [Fact]
    public void NFR01_H11_The_hook_layer_references_no_COM_UI_or_file_system_types()
    {
        var dir = Path.Combine(RepoFiles.AppDir, "Explorer");
        foreach (var name in HookLayerFiles)
        {
            var path = Path.Combine(dir, name);
            File.Exists(path).ShouldBeTrue(path);
            var text = StripComments(File.ReadAllText(path));
            foreach (var (what, pattern) in BannedInHookLayer)
            {
                pattern.IsMatch(text).ShouldBeFalse($"{name} must not use {what} (hook thread never makes COM, UI or file calls, 04 B.7)");
            }
        }
    }

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

    /// <summary>Comments and the text of string literals are not code: a log message may name a type.</summary>
    private static string StripComments(string source) =>
        Regex.Replace(
            Regex.Replace(Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline), @"//[^\n]*", ""),
            "\"(?:[^\"\\\\\\r\\n]|\\\\.)*\"",
            "\"\"");
}
