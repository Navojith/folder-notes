using FolderNotes.App.Controls;
using FolderNotes.App.Themes;
using FolderNotes.Core.Domain;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace FolderNotes.App;

public sealed partial class MainWindow : Window, IThemeTarget
{
    private readonly ThemeService _theme;
    private bool _syncingChoice;

    internal MainWindow(ThemeService theme)
    {
        _theme = theme ?? throw new ArgumentNullException(nameof(theme));
        InitializeComponent();
        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "FolderNotes.ico"));
        BuildTokenSample();
        Root.ActualThemeChanged += (_, _) => PaintAccents();
        _theme.ThemeChanged += OnThemeChanged;
        Closed += (_, _) => _theme.ThemeChanged -= OnThemeChanged;
    }

    void IThemeTarget.ApplyTheme(AppTheme theme)
    {
        Root.RequestedTheme = ThemeService.ToElementTheme(theme);
        AppWindow.TitleBar.PreferredTheme = theme switch
        {
            AppTheme.Light => TitleBarTheme.Light,
            AppTheme.Dark => TitleBarTheme.Dark,
            _ => TitleBarTheme.UseDefaultAppMode,
        };
        _syncingChoice = true;
        ThemeChoice.SelectedIndex = (int)theme;
        _syncingChoice = false;
    }

    internal void ShowRequest(string text)
    {
        RequestText.Text = text;
        RequestText.Visibility = Visibility.Visible;
    }

    internal void ShowError(string? message)
    {
        ErrorBar.Message = message ?? string.Empty;
        ErrorBar.IsOpen = message is not null;
    }

    private void OnThemeChoiceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncingChoice && ThemeChoice.SelectedIndex >= 0)
        {
            _theme.Theme = (AppTheme)ThemeChoice.SelectedIndex;
        }
    }

    private void OnThemeChanged(object? sender, EventArgs e) => PaintAccents();

    private void BuildTokenSample()
    {
        foreach (var visual in StatusVisuals.All)
        {
            if (visual.HasPill)
            {
                StatusSample.Children.Add(new StatusPill { Status = visual.Status });
            }
        }

        foreach (var key in Palette.Keys)
        {
            LabelSample.Children.Add(new LabelChip { LabelName = key, ColorKey = key, HorizontalAlignment = HorizontalAlignment.Left });
            var bar = new Rectangle { Width = 4, Height = 28, RadiusX = 2, RadiusY = 2, Tag = key };
            AutomationProperties.SetName(bar, "Accent: " + key);
            AccentSample.Children.Add(bar);
        }

        PaintAccents();
    }

    private void PaintAccents()
    {
        foreach (var bar in AccentSample.Children.OfType<Rectangle>())
        {
            bar.Fill = ThemeTokens.GetThemed<Brush>(Root, ThemeTokens.LabelAccentBrush((string)bar.Tag));
        }
    }
}
