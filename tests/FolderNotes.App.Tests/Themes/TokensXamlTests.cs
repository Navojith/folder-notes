using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FolderNotes.App.Themes;
using FolderNotes.Core.Domain;

namespace FolderNotes.App.Tests.Themes;

/// <summary>REQ-082 / REQ-083: Tokens.xaml (specs/05 § Design tokens, ADR-013) is complete and AA-compliant.</summary>
public sealed partial class TokensXamlTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace P = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly string[] s_tokens = ["LabelAccent", "LabelChipBackground", "LabelChipForeground"];

    // Layer backgrounds from 05: the accent must reach 3:1 against each.
    private static readonly Dictionary<string, string[]> s_layers = new()
    {
        ["Light"] = ["#FFF3F3F3", "#FFFFFFFF"],
        ["Dark"] = ["#FF202020", "#FF2C2C2C", "#FF333333"],
    };

    private static readonly XDocument s_doc = XDocument.Load(RepoFiles.TokensPath);

    public static TheoryData<string, string> ThemesAndKeys()
    {
        var data = new TheoryData<string, string>();
        foreach (var theme in new[] { "Light", "Dark" })
        {
            foreach (var key in Palette.Keys)
            {
                data.Add(theme, key);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ThemesAndKeys))]
    public void REQ083_ChipText_HasAtLeast4_5To1OnTheChip(string theme, string key)
    {
        var fg = ColorOf(theme, $"LabelChipForeground.{key}");
        var bg = ColorOf(theme, $"LabelChipBackground.{key}");
        Contrast(fg, bg).ShouldBeGreaterThanOrEqualTo(4.5, $"{theme} {key}");
    }

    [Theory]
    [MemberData(nameof(ThemesAndKeys))]
    public void REQ083_Accent_HasAtLeast3To1OnEveryLayer(string theme, string key)
    {
        var accent = ColorOf(theme, $"LabelAccent.{key}");
        foreach (var layer in s_layers[theme])
        {
            Contrast(accent, Parse(layer)).ShouldBeGreaterThanOrEqualTo(3.0, $"{theme} {key} on {layer}");
        }
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    [InlineData("HighContrast")]
    public void REQ082_EveryThemeDefinesEveryTokenAndBrush(string theme)
    {
        var keys = Theme(theme).Elements().Select(e => (string?)e.Attribute(X + "Key")).ToHashSet();
        foreach (var key in Palette.Keys)
        {
            foreach (var token in s_tokens)
            {
                keys.ShouldContain($"{token}.{key}");
                keys.ShouldContain($"{token}Brush.{key}");
            }
        }

        keys.ShouldContain("ChipBorderBrush");
        keys.ShouldContain("ChipBorderThickness");
    }

    [Fact]
    public void REQ083_HighContrast_UsesOnlySystemColours()
    {
        var hc = Theme("HighContrast");
        hc.Descendants().Where(e => e.Name == P + "Color").ShouldBeEmpty();
        foreach (var alias in hc.Elements(P + "StaticResource"))
        {
            ((string?)alias.Attribute("ResourceKey")).ShouldNotBeNull().ShouldStartWith("SystemColor");
        }

        foreach (var attr in hc.Descendants().Attributes())
        {
            HexColor().IsMatch(attr.Value).ShouldBeFalse($"{attr.Parent?.Attribute(X + "Key")?.Value}: {attr.Value}");
        }
    }

    [Fact]
    public void REQ082_ThemeDictionaries_AreExactlyLightDarkHighContrast() =>
        ThemeDictionaries().Select(d => (string?)d.Attribute(X + "Key")).Order(StringComparer.Ordinal)
            .ShouldBe(["Dark", "HighContrast", "Light"]);

    [Fact]
    public void REQ082_EveryStatusIconKey_HasPathGeometry()
    {
        var strings = s_doc.Root!.Elements(X + "String").ToDictionary(e => (string)e.Attribute(X + "Key")!, e => e.Value);
        foreach (var visual in StatusVisuals.All.Where(v => v.HasPill))
        {
            strings.ShouldContainKey(visual.IconKey!);
            PathData().IsMatch(strings[visual.IconKey!]).ShouldBeTrue(visual.IconKey);
        }
    }

    [Fact]
    public void ThemeTokens_KeyBuilders_MatchTheFile()
    {
        var keys = Theme("Light").Elements().Select(e => (string?)e.Attribute(X + "Key")).ToHashSet();
        keys.ShouldContain(ThemeTokens.LabelAccent("red"));
        keys.ShouldContain(ThemeTokens.LabelAccentBrush("red"));
        keys.ShouldContain(ThemeTokens.LabelChipBackground("teal"));
        keys.ShouldContain(ThemeTokens.LabelChipBackgroundBrush("teal"));
        keys.ShouldContain(ThemeTokens.LabelChipForeground("pink"));
        keys.ShouldContain(ThemeTokens.LabelChipForegroundBrush("pink"));
        Should.Throw<ArgumentException>(() => ThemeTokens.LabelAccent("magenta"));
    }

    [Theory]
    [InlineData("#FF000000", "#FFFFFFFF", 21.0)]
    [InlineData("#FF777777", "#FFFFFFFF", 4.48)]
    [InlineData("#FFFFFFFF", "#FFFFFFFF", 1.0)]
    public void ContrastFormula_MatchesWcagReferenceValues(string a, string b, double expected) =>
        Contrast(Parse(a), Parse(b)).ShouldBe(expected, 0.01);

    private static IEnumerable<XElement> ThemeDictionaries() =>
        s_doc.Root!.Element(P + "ResourceDictionary.ThemeDictionaries")!.Elements(P + "ResourceDictionary");

    private static XElement Theme(string name) =>
        ThemeDictionaries().Single(d => (string?)d.Attribute(X + "Key") == name);

    private static (double R, double G, double B) ColorOf(string theme, string key)
    {
        var element = Theme(theme).Elements(P + "Color").SingleOrDefault(e => (string?)e.Attribute(X + "Key") == key);
        element.ShouldNotBeNull($"{theme}: {key}");
        return Parse(element.Value.Trim());
    }

    private static (double R, double G, double B) Parse(string hex)
    {
        HexColor().IsMatch(hex).ShouldBeTrue(hex);
        var h = hex.TrimStart('#');
        if (h.Length == 8)
        {
            h[..2].ShouldBe("FF", $"{hex}: token colours must be opaque for the contrast rules to hold");
            h = h[2..];
        }

        return (Channel(h[..2]), Channel(h[2..4]), Channel(h[4..]));
    }

    private static double Channel(string hex) => int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;

    // WCAG 2.1 relative luminance and contrast ratio.
    private static double Luminance((double R, double G, double B) c)
    {
        static double Lin(double v) => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        return (0.2126 * Lin(c.R)) + (0.7152 * Lin(c.G)) + (0.0722 * Lin(c.B));
    }

    private static double Contrast((double R, double G, double B) a, (double R, double G, double B) b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    [GeneratedRegex("^#([0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$")]
    private static partial Regex HexColor();

    [GeneratedRegex(@"^M[0-9MmLlHhVvCcSsQqTtAaZz.,\s-]+$")]
    private static partial Regex PathData();
}
