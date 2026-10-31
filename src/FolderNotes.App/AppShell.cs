using FolderNotes.App.Activation;
using Microsoft.UI.Windowing;
using Serilog;

namespace FolderNotes.App;

/// <summary>
/// Owns the main window: created on demand, destroyed on close so the tray process stays light
/// (REQ-080, specs/02 § App shell). Every member runs on the UI thread.
/// </summary>
internal sealed class AppShell(ILogger log) : IAppShell
{
    private readonly ILogger _log = (log ?? throw new ArgumentNullException(nameof(log))).ForContext<AppShell>();
    private MainWindow? _main;
    private string? _startupError;

    /// <summary>Shown in every new main window (e.g. the database couldn't be opened).</summary>
    public string? StartupError
    {
        get => _startupError;
        set
        {
            _startupError = value;
            _main?.ShowError(value);
        }
    }

    public bool IsMainWindowOpen => _main is not null;

    public void ShowMainWindow() => Show(null);

    // T2.3 replaces these with the editor; T2.6 with the search palette.
    public void OpenEditor(string folderPath) => Show($"Editor requested for: {folderPath}");

    public void OpenSearch(string query) => Show(query.Length == 0 ? "Search requested" : $"Search requested: {query}");

    public void OpenNote(Guid noteId) => Show($"Note requested: {noteId}");

    public void CloseMainWindow() => _main?.Close();

    private void Show(string? request)
    {
        if (_main is null)
        {
            _main = new MainWindow();
            _main.Closed += (_, _) =>
            {
                _main = null;
                _log.Debug("Main window closed; staying in the tray");
            };
            _main.ShowError(_startupError);
        }

        if (request is not null)
        {
            _main.ShowRequest(request);
        }

        if (_main.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore();
        }

        _main.Activate();
    }
}
