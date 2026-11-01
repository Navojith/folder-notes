using System.Xml.Linq;

namespace FolderNotes.App.Tests.Accessibility;

/// <summary>REQ-083 / 05 § Accessibility: every interactive element in the checked-in XAML has a name a screen reader can say.</summary>
public sealed class UiaNamesTests
{
    private static readonly HashSet<string> Interactive = new(StringComparer.Ordinal)
    {
        "Button", "HyperlinkButton", "AppBarButton", "AppBarToggleButton", "ToggleButton", "TextBox", "AutoSuggestBox",
        "ComboBox", "CalendarDatePicker", "ToggleSwitch", "RadioButtons", "CheckBox", "ListView", "GridView",
        "SelectorBar", "NavigationViewItem", "MenuFlyoutItem",
    };

    [Fact]
    public void REQ083_EveryInteractiveXamlElement_HasAName()
    {
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(RepoFilesDir(), "*.xaml", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(RepoFilesDir(), file);
            if (rel.StartsWith("bin", StringComparison.OrdinalIgnoreCase) || rel.StartsWith("obj", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var doc = XDocument.Load(file, LoadOptions.SetLineInfo);
            foreach (var e in doc.Descendants().Where(e => Interactive.Contains(e.Name.LocalName)))
            {
                if (!HasName(e))
                {
                    var line = ((System.Xml.IXmlLineInfo)e).LineNumber;
                    offenders.Add($"{rel}:{line}: <{e.Name.LocalName} x:Name={(string?)e.Attributes().FirstOrDefault(a => a.Name.LocalName == "Name")}>");
                }
            }
        }

        offenders.ShouldBeEmpty();
    }

    [Fact]
    public void Checker_FlagsAnIconOnlyButton()
    {
        HasName(XElement.Parse("<Button><SymbolIcon Symbol=\"Delete\" /></Button>")).ShouldBeFalse();
        HasName(XElement.Parse("<Button AutomationProperties.Name=\"Delete\"><SymbolIcon Symbol=\"Delete\" /></Button>")).ShouldBeTrue();
        HasName(XElement.Parse("<Button><StackPanel><SymbolIcon /><TextBlock Text=\"Go\" /></StackPanel></Button>")).ShouldBeTrue();
        HasName(XElement.Parse("<TextBox PlaceholderText=\"Name\" />")).ShouldBeFalse();
    }

    private static bool HasName(XElement e)
    {
        foreach (var a in e.Attributes())
        {
            var n = a.Name.LocalName;
            if (n is "AutomationProperties.Name" or "Header" or "Content" or "Label" or "Text" or "LabeledBy"
                or "AutomationProperties.LabeledBy")
            {
                return !string.IsNullOrWhiteSpace(a.Value);
            }
        }

        // Content given as child markup: needs some text in it.
        return e.Descendants().Any(d => d.Name.LocalName is "TextBlock" or "Run" && d.Attributes().Any(a => a.Name.LocalName == "Text" && a.Value.Length > 0));
    }

    private static string RepoFilesDir() => Themes.RepoFiles.AppDir;
}
