using FolderNotes.Core.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace FolderNotes.App.Controls;

/// <summary>A flyout with the 12 palette swatches (UIA name = colour key, the current one selected).</summary>
internal static class ColorPickerFlyout
{
    public static Flyout Create(string current, Action<string> picked)
    {
        ArgumentNullException.ThrowIfNull(picked);
        var flyout = new Flyout();
        var grid = new GridView
        {
            SelectionMode = ListViewSelectionMode.Single,
            Padding = new Thickness(0),
            Width = 6 * 34,
        };
        AutomationProperties.SetName(grid, "Colour");
        foreach (var key in Palette.Keys)
        {
            var item = new GridViewItem
            {
                Content = new ColorSwatch { ColorKey = key },
                Tag = key,
                MinWidth = 30,
                MinHeight = 30,
                Margin = new Thickness(0, 0, 2, 2),
                Padding = new Thickness(0),
            };
            AutomationProperties.SetName(item, key);
            grid.Items.Add(item);
        }

        grid.SelectedIndex = Math.Max(0, Palette.Keys.ToList().IndexOf(current));
        grid.SelectionChanged += (_, _) =>
        {
            if (grid.SelectedItem is GridViewItem { Tag: string key })
            {
                flyout.Hide();
                picked(key);
            }
        };
        flyout.Content = grid;
        return flyout;
    }
}
