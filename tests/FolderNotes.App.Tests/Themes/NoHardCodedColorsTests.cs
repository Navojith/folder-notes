using System.Text.RegularExpressions;

namespace FolderNotes.App.Tests.Themes;

/// <summary>REQ-082 / 05 § Design tokens: colours live only in Themes/Tokens.xaml, so HC and dark mode can't be bypassed.</summary>
public sealed partial class NoHardCodedColorsTests
{
    [Fact]
    public void REQ082_AppSources_HaveNoHardCodedColoursOutsideTokens()
    {
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(RepoFiles.AppDir, "*.*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(file);
            var rel = Path.GetRelativePath(RepoFiles.AppDir, file);
            if ((ext != ".xaml" && ext != ".cs")
                || rel.StartsWith("bin", StringComparison.OrdinalIgnoreCase)
                || rel.StartsWith("obj", StringComparison.OrdinalIgnoreCase)
                || rel.Equals(Path.Combine("Themes", "Tokens.xaml"), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var pattern = ext == ".xaml" ? XamlColor() : CodeColor();
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (pattern.IsMatch(lines[i]))
                {
                    offenders.Add($"{rel}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        offenders.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("<Border Background=\"#FF0000\" />", true)]
    [InlineData("<Color x:Key=\"X\">#FFAA0000</Color>", true)]
    [InlineData("<TextBlock Foreground=\"Red\" />", true)]
    [InlineData("<Border Background=\"{ThemeResource CardBackgroundFillColorDefaultBrush}\" />", false)]
    [InlineData("<Border Background=\"Transparent\" />", false)]
    public void XamlPattern_CatchesLiteralColours(string line, bool expected) => XamlColor().IsMatch(line).ShouldBe(expected);

    [Theory]
    [InlineData("var b = new SolidColorBrush(Colors.Red);", true)]
    [InlineData("Color.FromArgb(255, 1, 2, 3)", true)]
    [InlineData("const uint WM_APP = 0x8000;", false)]
    [InlineData("#pragma warning disable CA1031", false)]
    public void CodePattern_CatchesLiteralColours(string line, bool expected) => CodeColor().IsMatch(line).ShouldBe(expected);

    // A hex literal, or a named colour in a colour-ish attribute.
    [GeneratedRegex(@"""#[0-9A-Fa-f]{3,8}""|>#[0-9A-Fa-f]{3,8}<|\b(Color|Background|Foreground|Fill|Stroke|BorderBrush)=""(?!\{)(?!Transparent"")[A-Za-z]+""")]
    private static partial Regex XamlColor();

    [GeneratedRegex(@"\bColors\.\w+|\bColor\.FromArgb|ColorHelper\.FromArgb|""#[0-9A-Fa-f]{6,8}""")]
    private static partial Regex CodeColor();
}
