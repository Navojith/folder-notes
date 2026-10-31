using FolderNotes.App.Activation;
using FolderNotes.App.Editor;
using FolderNotes.App.Themes;
using Microsoft.UI.Windowing;
using Serilog;

namespace FolderNotes.App;

/// <summary>
/// Owns the windows: the main window and the compact editors, created on demand and destroyed on
/// close so the tray process stays light (REQ-080, specs/02 § App shell, § Editor). UI thread only.
/// </summary>
internal sealed class AppShell(ThemeService theme, ILogger log) : IAppShell
{
    private readonly ThemeService _theme = theme ?? throw new ArgumentNullException(nameof(theme));
    private readonly ILogger _log = (log ?? throw new ArgumentNullException(nameof(log))).ForContext<AppShell>();
    private readonly List<EditorWindow> _editors = [];
    private EditorWindow? _spareEditor; // hidden, kept for reuse (REQ-080)
    private MainWindow? _main;
    private string? _startupError;

    /// <summary>Creates an editor session. Null when the database couldn't be opened.</summary>
    public Func<NoteEditorViewModel>? EditorFactory { get; set; }

    public int OpenEditorCount => _editors.Count;

    /// <summary>Runs once the last window has closed (REQ-080).</summary>
    public Services.IdleTrim? IdleTrim { get; set; }

    private bool AllClosed => _main is null && _editors.Count == 0;

    private void OnWindowClosed()
    {
        if (AllClosed)
        {
            IdleTrim?.Schedule(() => AllClosed);
        }
    }

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

    public void OpenEditor(string folderPath) =>
        OpenEditorWindow(vm => vm.LoadForFolderAsync(folderPath), $"--edit {folderPath}");

    // T2.6 replaces this with the search palette.
    public void OpenSearch(string query) => Show(query.Length == 0 ? "Search requested" : $"Search requested: {query}");

    // T2.4 moves this to the main window's detail pane.
    public void OpenNote(Guid noteId) =>
        OpenEditorWindow(vm => vm.LoadByIdAsync(noteId), $"--open-note {noteId}");

    public void CloseMainWindow() => _main?.Close();

    /// <summary>Quit: every editor saves what it has (REQ-005) and closes.</summary>
    public async Task CloseEditorsAsync()
    {
        foreach (var editor in _editors.ToArray())
        {
            await editor.FlushAndCloseAsync(explicitSave: false); // saves, then hides
        }

        _spareEditor?.CloseForGood();
        foreach (var editor in _editors.ToArray())
        {
            editor.CloseForGood();
        }
    }

    private async void OpenEditorWindow(Func<NoteEditorViewModel, Task> load, string what)
    {
        if (EditorFactory is null)
        {
            Show(_startupError ?? "The notes database isn't available.");
            return;
        }

        var vm = EditorFactory();
        try
        {
            await load(vm);
        }
#pragma warning disable CA1031 // Startup-style failure: log and tell the user instead of crashing the tray.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log.Error(ex, "Loading the editor for {What} failed", what);
            vm.Dispose();
            Show($"Couldn't open the note: {ex.Message}");
            return;
        }

        // One editor per note / folder (05 § Editor behaviour).
        var existing = vm.State == EditorState.Editing
            ? _editors.FirstOrDefault(w => w.ViewModel.State is EditorState.Editing or EditorState.Deleted
                && (w.ViewModel.NoteId == vm.NoteId || string.Equals(w.ViewModel.FolderKey, vm.FolderKey, StringComparison.Ordinal)))
            : null;
        if (existing is not null)
        {
            vm.Dispose();
            Bring(existing);
            return;
        }

        EditorWindow window;
        if (_spareEditor is { } spare)
        {
            _spareEditor = null;
            window = spare;
            window.Attach(vm);
        }
        else
        {
            window = new EditorWindow(vm, _log);
            _theme.Register(window);
            window.Hidden += (_, _) => OnEditorHidden(window);
            window.Closed += (_, _) => OnEditorDestroyed(window);
        }

        _editors.Add(window);
        window.ShowAndFocus();
    }

    /// <summary>The user closed an editor: keep one hidden window for the next one, destroy the rest.</summary>
    private void OnEditorHidden(EditorWindow window)
    {
        _editors.Remove(window);
        window.ViewModel.Dispose();
        if (_spareEditor is null)
        {
            _spareEditor = window;
        }
        else
        {
            window.CloseForGood();
        }

        _log.Debug("Editor closed ({Open} still open)", _editors.Count);
        OnWindowClosed();
    }

    private void OnEditorDestroyed(EditorWindow window)
    {
        _theme.Unregister(window);
        _editors.Remove(window);
        if (ReferenceEquals(_spareEditor, window))
        {
            _spareEditor = null;
        }

        window.ViewModel.Dispose();
    }

    private static void Bring(EditorWindow window)
    {
        if (window.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore();
        }

        window.ShowAndFocus();
    }

    private void Show(string? request)
    {
        if (_main is null)
        {
            var window = _main = new MainWindow(_theme);
            _theme.Register(window);
            window.Closed += (_, _) =>
            {
                _theme.Unregister(window);
                _main = null;
                _log.Debug("Main window closed; staying in the tray");
                OnWindowClosed();
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
