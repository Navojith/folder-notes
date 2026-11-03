namespace FolderNotes.App.Tests.Themes;

/// <summary>
/// Source text must not contain control characters other than tab, CR and LF. Generated edits have slipped in BEL, backspace
/// and 0x01 several times (an escape such as <c>\a</c> or <c>\b</c> interpreted on the way); once they made three guard
/// regexes vacuous (T3.2.5). This scan catches them in source, tests and specs.
/// </summary>
[Trait("Kind", "Deterministic")]
public class ControlCharacterGuardTests
{
    private static readonly string[] Extensions = [".cs", ".md", ".xaml", ".cpp", ".h", ".ps1", ".json", ".props", ".csproj", ".editorconfig"];

    private static readonly string[] SkippedDirectories = ["bin", "obj", ".git", ".vs", "artifacts", "node_modules"];

    [Fact]
    public void Repo_text_files_contain_no_control_characters()
    {
        var offenders = new List<string>();
        foreach (var top in new[] { "src", "tests", "specs", "spikes", "eng" })
        {
            var dir = Path.Combine(RepoFiles.Root, top);
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(RepoFiles.Root, file);
                if (!Extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)
                    || rel.Split(Path.DirectorySeparatorChar).Any(part => SkippedDirectories.Contains(part, StringComparer.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (lines[i].Any(c => c < ' ' && c != '\t'))
                    {
                        offenders.Add($"{rel}:{i + 1}: U+{(int)lines[i].First(c => c < ' ' && c != '\t'):X4}");
                    }
                }
            }
        }

        offenders.ShouldBeEmpty();
    }
}
