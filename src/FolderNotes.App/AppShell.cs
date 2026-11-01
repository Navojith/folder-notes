using FolderNotes.App.Activation;
using FolderNotes.App.Editor;
using FolderNotes.App.Themes;
using Microsoft.UI.Windowing;
using Serilog;

namespace FolderNotes.App;

/// <summary>
/// Owns the windows: the main window and the compact editors, created on demand and hidden for
/// reuse on close (ADR-015; specs/02 § App shell, § Editor, § Library). UI thread only.
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

    /// <summary>Creates the main window's library. Null when the database couldn't be opened.</summary>
    public Func<Library.LibraryViewModel>? LibraryFactory { get; set; }

    /// <summary>Creates the search palette's view model. Null when the database couldn't be opened.</summary>
    public Func<Search.SearchPaletteViewModel>? SearchFactory { get; set; }

    private Search.SearchPaletteWindow? _palette;

    /// <summary>Creates the main window's Labels page. Null when the database couldn't be opened.</summary>
    public Func<Labels.LabelsViewModel>? LabelsFactory { get; set; }

    /// <summary>Creates the main window's Orphaned page. Null when the database couldn't be opened.</summary>
    public Func<Orphans.OrphansViewModel>? OrphansFactory { get; set; }

    public int OpenEditorCount => _editors.Count;

    /// <summary>Label changes (ADR-018) and relinks of their note (ADR-020) reach every open compact editor.</summary>
    public Services.INoteEvents? NoteEvents
    {
        set
        {
            if (value is not null)
            {
                value.LabelsChanged += OnLabelsChanged;
                value.NoteChanged += OnNoteChanged;
            }
        }
    }

    private async void OnNoteChanged(object? sender, Core.Storage.NoteChangedEventArgs e)
    {
        if (e.Kind is not (Core.Storage.NoteChangeKind.Relinked or Core.Storage.NoteChangeKind.OrphanStateChanged))
        {
            return;
        }

        foreach (var editor in _editors.Where(w => w.ViewModel.NoteId == e.NoteId).ToArray())
        {
            try
            {
                await editor.ViewModel.RefreshFolderAsync();
            }
#pragma warning disable CA1031 // One editor failing to refresh must not stop the others.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _log.Error(ex, "Refreshing an editor's folder failed");
            }
        }
    }

    private async void OnLabelsChanged(object? sender, EventArgs e)
    {
        foreach (var editor in _editors.ToArray())
        {
            try
            {
                await editor.ViewModel.RefreshLabelsAsync();
            }
#pragma warning disable CA1031 // One editor failing to refresh must not stop the others.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _log.Error(ex, "Refreshing an editor's labels failed");
            }
        }
    }

    /// <summary>Runs once the last window has closed (REQ-080).</summary>
    public Services.IdleTrim? IdleTrim { get; set; }

    private bool AllClosed => (_main is null || !_main.IsShown) && (_palette is null || !_palette.IsShown) && _editors.Count == 0;

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

    public bool IsMainWindowOpen => _main?.IsShown == true;

    public void ShowMainWindow() => Show(null);

    public void OpenEditor(string folderPath) =>
        OpenEditorWindow(vm => vm.LoadForFolderAsync(folderPath), $"--edit {folderPath}");

    /// <summary>The search palette (05 § Search palette behaviour, ADR-019): created once, then re-shown.</summary>
    public async void OpenSearch(string query)
    {
        if (SearchFactory is null)
        {
            Show(_startupError ?? "The notes database isn't available.");
            return;
        }

        if (_palette is null)
        {
            var palette = _palette = new Search.SearchPaletteWindow(SearchFactory(), _log);
            _theme.Register(palette);
            palette.ViewModel.OpenNoteRequested += (_, id) => OpenNote(id);
            palette.Hidden += (_, _) => OnWindowClosed();
            palette.Closed += (_, _) =>
            {
                _theme.Unregister(palette);
                palette.ViewModel.Dispose();
            };
        }

        try
        {
            await _palette.ShowAsync(query ?? "");
        }
#pragma warning disable CA1031 // Tell the user instead of crashing the tray.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log.Error(ex, "Opening the search palette failed");
        }
    }

    /// <summary><c>--open-note</c>: the main window's library selects the note (05 § Library behaviour).</summary>
    public async void OpenNote(Guid noteId)
    {
        var main = Show(null);
        if (main.LibraryViewModel is not { } library)
        {
            return; // no database: the window shows the startup error
        }

        main.ShowLibrary();
        try
        {
            await library.OpenNoteAsync(noteId);
        }
#pragma warning disable CA1031 // Tell the user instead of crashing the tray.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log.Error(ex, "Opening note {NoteId} in the library failed", noteId);
            main.ShowError($"Couldn't open the note: {ex.Message}");
        }
    }

    /// <summary>Quit: flush the library's editor and destroy the main window (and the palette).</summary>
    public async Task CloseMainWindowAsync()
    {
        _palette?.CloseForGood();
        _palette = null;
        if (_main is { } main)
        {
            _main = null;
            await main.CloseForGoodAsync();
            main.LibraryViewModel?.Dispose();
            main.LabelsViewModel?.Dispose();
            main.OrphansViewModel?.Dispose();
        }
    }

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

        // One editor per note / folder (05 § Editor behaviour), and per missing folder path (O-17).
        var existing = vm.State switch
        {
            EditorState.Editing => _editors.FirstOrDefault(w => w.ViewModel.State is EditorState.Editing or EditorState.Deleted
                && (w.ViewModel.NoteId == vm.NoteId || string.Equals(w.ViewModel.FolderKey, vm.FolderKey, StringComparison.Ordinal))),
            EditorState.Missing => _editors.FirstOrDefault(w => w.ViewModel.State == EditorState.Missing
                && w.ViewModel.FolderPath.Length > 0
                && string.Equals(w.ViewModel.FolderPath, vm.FolderPath, StringComparison.OrdinalIgnoreCase)),
            _ => null,
        };
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

    private MainWindow Show(string? request)
    {
        if (_main is null)
        {
            var orphans = OrphansFactory?.Invoke();
            if (orphans is not null)
            {
                orphans.OpenNoteRequested += (_, id) => OpenNote(id);
            }

            var window = _main = new MainWindow(_theme, LibraryFactory?.Invoke(), LabelsFactory?.Invoke(), orphans, _log);
            _theme.Register(window);
            window.Hidden += (_, _) =>
            {
                _log.Debug("Main window hidden; staying in the tray");
                OnWindowClosed();
            };
            window.Closed += (_, _) => _theme.Unregister(window);
            window.ShowError(_startupError);
        }

        if (request is not null)
        {
            _main.ShowRequest(request);
        }

        _ = ShowMainAsync(_main);
        return _main;
    }

    private async Task ShowMainAsync(MainWindow window)
    {
        try
        {
            await window.ShowAsync();
        }
#pragma warning disable CA1031 // A failed first load shows in the window rather than crashing the tray.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log.Error(ex, "Loading the library failed");
            window.ShowError($"Couldn't load the notes: {ex.Message}");
        }
    }
}
