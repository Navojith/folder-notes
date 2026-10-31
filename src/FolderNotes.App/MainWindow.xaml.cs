using Microsoft.UI.Xaml;

namespace FolderNotes.App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "FolderNotes.ico"));
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
}
