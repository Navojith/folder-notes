using Microsoft.UI.Xaml;

namespace FolderNotes.App;

/// <summary>
/// Application entry. Phase 2 (T2.1) adds single-instance, activation-arg routing and the tray icon.
/// </summary>
public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
