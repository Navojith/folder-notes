using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using FolderNotes.App.Editor;
using FolderNotes.App.Services;
using FolderNotes.App.Themes;
using FolderNotes.Core.Domain;
using FolderNotes.Core.Storage;
using Serilog;

namespace FolderNotes.App.Library;

public enum LibraryViewMode
{
    List,
    Cards,
}

public enum LibraryEmptyState
{
    /// <summary>There are rows (or the first load hasn't finished).</summary>
    None,

    /// <summary>"No notes yet".</summary>
    NoNotes,

    /// <summary>"No notes match these filters".</summary>
    NoMatches,
}

/// <summary>An active filter, shown as a removable chip (05 § Library behaviour).</summary>
public sealed record LibraryFilter(string? LabelName, NoteStatus? Status)
{
    public string Text => LabelName ?? StatusVisuals.For(Status ?? NoteStatus.None).Text;

    public string RemoveName => "Remove filter " + Text;
}

/// <summary>
/// The All notes library (specs/05 § Library behaviour, 02 § Library, ADR-017): query, rows,
/// view mode, selection and the detail editor. UI-thread only; no XAML types.
/// </summary>
public sealed partial class LibraryViewModel : ObservableObject, IDisposable
{
    private readonly INoteRepository _notes;
    private readonly ILabelRepository _labelStore;
    private readonly INoteEvents _events;
    private readonly Func<NoteEditorViewModel> _editorFactory;
    private readonly TimeProvider _clock;
    private readonly ILogger _log;
    private readonly TimeZoneInfo _zone;
    private readonly CultureInfo _culture;
    private readonly List<string> _labelFilters = [];
    private readonly List<NoteStatus> _statusFilters = [];
    private Task _reload = Task.CompletedTask;
    private bool _reloadAgain;
    private Task _labelsWork = Task.CompletedTask;
    private bool _loadedOnce;
    private bool _disposed;

    internal LibraryViewModel(
        INoteRepository notes,
        ILabelRepository labels,
        INoteEvents events,
        Func<NoteEditorViewModel> editorFactory,
        TimeProvider clock,
        ILogger log,
        TimeZoneInfo? zone = null,
        CultureInfo? culture = null)
    {
        _notes = notes ?? throw new ArgumentNullException(nameof(notes));
        _labelStore = labels ?? throw new ArgumentNullException(nameof(labels));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _editorFactory = editorFactory ?? throw new ArgumentNullException(nameof(editorFactory));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _log = (log ?? throw new ArgumentNullException(nameof(log))).ForContext<LibraryViewModel>();
        _zone = zone ?? TimeZoneInfo.Local;
        _culture = culture ?? CultureInfo.CurrentCulture;
        _events.NoteChanged += OnNoteChanged;
        _events.LabelsChanged += OnLabelsChanged;
    }

    /// <summary>The listed notes. Replaced as a whole on every reload (ADR-017).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText))]
    public partial ObservableCollection<LibraryRow> Rows { get; private set; } = [];

    [ObservableProperty]
    public partial NoteSort Sort { get; private set; } = NoteSort.UpdatedDesc;

    [ObservableProperty]
    public partial LibraryViewMode ViewMode { get; set; } = LibraryViewMode.List;

    [ObservableProperty]
    public partial LibraryEmptyState EmptyState { get; private set; }

    /// <summary>Every label, for the Filter menu.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<Label> AllLabels { get; private set; } = [];

    /// <summary>Active filters as chips: labels first, then statuses.</summary>
    public ObservableCollection<LibraryFilter> ActiveFilters { get; } = [];

    public bool HasFilters => ActiveFilters.Count > 0;

    public string CountText => Rows.Count == 1 ? "1 note" : Rows.Count.ToString(_culture) + " notes";

    /// <summary>The selected note's id, or null.</summary>
    [ObservableProperty]
    public partial Guid? SelectedId { get; private set; }

    /// <summary>The editor for the selected note, or null ("Select a note to see it here").</summary>
    [ObservableProperty]
    public partial NoteEditorViewModel? Detail { get; private set; }

    public IReadOnlyList<string> LabelFilters => _labelFilters;

    public IReadOnlyList<NoteStatus> StatusFilters => _statusFilters;

    public bool IsLabelFiltered(string name) => _labelFilters.Contains(name, Label.NameComparer);

    public bool IsStatusFiltered(NoteStatus status) => _statusFilters.Contains(status);

    // ───────────── loading ─────────────

    /// <summary>The window is shown: first load, or refresh the relative times.</summary>
    public Task ActivateAsync()
    {
        if (!_loadedOnce)
        {
            return ReloadAsync();
        }

        foreach (var row in Rows)
        {
            row.RefreshTimes();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Reloads labels and rows. Coalesced: while a reload runs, any number of calls cause exactly one
    /// more, and all of them complete when that one has (02 § Library).
    /// </summary>
    public Task ReloadAsync()
    {
        if (!_reload.IsCompleted)
        {
            _reloadAgain = true;
            return _reload;
        }

        _reload = RunReloadsAsync();
        return _reload;
    }

    /// <summary>Completes when no reload or label refresh is running or queued (tests).</summary>
    internal Task WhenIdleAsync() => Task.WhenAll(_reload, _labelsWork);

    private async Task RunReloadsAsync()
    {
        do
        {
            _reloadAgain = false;
            await ReloadOnceAsync();
        }
        while (_reloadAgain && !_disposed);
    }

    private async Task ReloadOnceAsync()
    {
        var labels = await _labelStore.ListLabelsAsync();
        AllLabels = labels;

        // A renamed or deleted label can't stay a filter (it would match nothing).
        var gone = _labelFilters.Where(f => !labels.Any(l => Label.NameComparer.Equals(l.Name, f))).ToList();
        foreach (var name in gone)
        {
            _labelFilters.Remove(name);
        }

        if (gone.Count > 0)
        {
            SyncFilterChips();
        }

        var query = new NoteQuery
        {
            State = NoteListState.Active,
            Labels = [.. _labelFilters],
            Statuses = [.. _statusFilters],
            Sort = Sort,
        };
        var notes = await _notes.ListAsync(query);
        Rows = new ObservableCollection<LibraryRow>(notes.Select(NewRow));
        _loadedOnce = true;
        EmptyState = Rows.Count > 0 ? LibraryEmptyState.None : HasFilters ? LibraryEmptyState.NoMatches : LibraryEmptyState.NoNotes;
    }

    private LibraryRow NewRow(Note note) => new(note, _clock, _zone, _culture);

    // ───────────── sort, filters, view ─────────────

    public Task SetSortAsync(NoteSort sort)
    {
        if (sort == Sort)
        {
            return Task.CompletedTask;
        }

        Sort = sort;
        return ReloadAsync();
    }

    public Task ToggleLabelFilterAsync(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var existing = _labelFilters.FirstOrDefault(f => Label.NameComparer.Equals(f, name));
        if (existing is null)
        {
            _labelFilters.Add(name);
        }
        else
        {
            _labelFilters.Remove(existing);
        }

        SyncFilterChips();
        return ReloadAsync();
    }

    public Task ToggleStatusFilterAsync(NoteStatus status)
    {
        if (status == NoteStatus.None)
        {
            throw new ArgumentOutOfRangeException(nameof(status), "There is no filter for 'none' (05).");
        }

        if (!_statusFilters.Remove(status))
        {
            _statusFilters.Add(status);
        }

        SyncFilterChips();
        return ReloadAsync();
    }

    public Task RemoveFilterAsync(LibraryFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        return filter.LabelName is { } name ? ToggleLabelFilterAsync(name) : ToggleStatusFilterAsync(filter.Status!.Value);
    }

    /// <summary>"Show notes" on the Labels page: only this label, every other filter cleared (05).</summary>
    public Task ShowOnlyLabelAsync(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _labelFilters.Clear();
        _statusFilters.Clear();
        _labelFilters.Add(name);
        SyncFilterChips();
        return ReloadAsync();
    }

    public Task ClearFiltersAsync()
    {
        if (!HasFilters)
        {
            return Task.CompletedTask;
        }

        _labelFilters.Clear();
        _statusFilters.Clear();
        SyncFilterChips();
        return ReloadAsync();
    }

    private void SyncFilterChips()
    {
        ActiveFilters.Clear();
        foreach (var name in _labelFilters)
        {
            ActiveFilters.Add(new LibraryFilter(name, null));
        }

        foreach (var status in _statusFilters.Order())
        {
            ActiveFilters.Add(new LibraryFilter(null, status));
        }

        OnPropertyChanged(nameof(HasFilters));
    }

    // ───────────── selection + detail ─────────────

    /// <summary>
    /// Shows <paramref name="id"/> in the detail pane (null = nothing selected). The current detail
    /// is flushed first, with the compact window's rules (05 § Editor behaviour).
    /// </summary>
    public async Task SelectAsync(Guid? id)
    {
        if (id == SelectedId && (id is null || Detail is not null))
        {
            return;
        }

        await CloseDetailAsync();
        SelectedId = id;
        if (id is not { } noteId)
        {
            return;
        }

        var editor = _editorFactory();
        editor.UndoExpired += OnDetailUndoExpired;
        Detail = editor;
        await editor.LoadByIdAsync(noteId);
    }

    /// <summary>
    /// <c>--open-note</c>: select the note, clearing the filters first if they hide it (05).
    /// </summary>
    public async Task OpenNoteAsync(Guid id)
    {
        await _reload; // a first load in flight
        if (!_loadedOnce)
        {
            await ReloadAsync();
        }

        if (Rows.All(r => r.Id != id) && HasFilters)
        {
            var note = await _notes.GetByIdAsync(id);
            if (note is { IsDeleted: false })
            {
                await ClearFiltersAsync();
            }
        }

        if (SelectedId == id)
        {
            SelectedId = null; // force a fresh session, e.g. after the note was edited elsewhere
        }

        await SelectAsync(id);
    }

    /// <summary>Flushes the detail editor (window hidden, Quit).</summary>
    public Task FlushAsync() => Detail?.FlushAsync() ?? Task.CompletedTask;

    private async Task CloseDetailAsync()
    {
        if (Detail is not { } old)
        {
            return;
        }

        Detail = null;
        old.UndoExpired -= OnDetailUndoExpired;
        try
        {
            await old.FlushAsync();
        }
#pragma warning disable CA1031 // The editor already logged and showed the error; switching notes must still work.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log.Error(ex, "Flushing the detail editor failed");
        }

        old.Dispose();
    }

    private async void OnDetailUndoExpired(object? sender, EventArgs e)
    {
        if (ReferenceEquals(sender, Detail))
        {
            await SelectAsync(null);
        }
    }

    // ───────────── keeping in sync (ADR-017) ─────────────

    private async void OnNoteChanged(object? sender, NoteChangedEventArgs e)
    {
        try
        {
            await ApplyChangeAsync(e);
        }
#pragma warning disable CA1031 // An event handler must not crash the UI thread; the next reload repairs the list.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log.Error(ex, "Updating the library after {Kind} of {NoteId} failed", e.Kind, e.NoteId);
        }
    }

    internal async Task ApplyChangeAsync(NoteChangedEventArgs e)
    {
        if (_disposed || !_loadedOnce)
        {
            return;
        }

        switch (e.Kind)
        {
            case NoteChangeKind.Updated:
                if (Rows.FirstOrDefault(r => r.Id == e.NoteId) is { } row
                    && await _notes.GetByIdAsync(e.NoteId) is { IsDeleted: false } note)
                {
                    row.Update(note);
                }

                break;
            case NoteChangeKind.Deleted:
            case NoteChangeKind.Purged:
                if (Rows.FirstOrDefault(r => r.Id == e.NoteId) is { } gone)
                {
                    Rows.Remove(gone);
                    OnPropertyChanged(nameof(CountText));
                    if (Rows.Count == 0)
                    {
                        EmptyState = HasFilters ? LibraryEmptyState.NoMatches : LibraryEmptyState.NoNotes;
                    }
                }

                break;
            case NoteChangeKind.Relinked or NoteChangeKind.OrphanStateChanged:
                var reload = ReloadAsync();
                if (Detail is { } detail && detail.NoteId == e.NoteId)
                {
                    await detail.RefreshFolderAsync(); // ADR-020
                }

                await reload;
                break;
            default: // Created, Restored
                await ReloadAsync();
                break;
        }
    }

    private async void OnLabelsChanged(object? sender, EventArgs e)
    {
        var work = OnLabelsChangedAsync();
        _labelsWork = work;
        try
        {
            await work;
        }
#pragma warning disable CA1031 // See OnNoteChanged.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log.Error(ex, "Updating the library after a label change failed");
        }
    }

    private async Task OnLabelsChangedAsync()
    {
        if (_disposed)
        {
            return;
        }

        var reload = _loadedOnce ? ReloadAsync() : Task.CompletedTask;
        if (Detail is { } detail)
        {
            await detail.RefreshLabelsAsync(); // ADR-018
        }

        await reload;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _events.NoteChanged -= OnNoteChanged;
        _events.LabelsChanged -= OnLabelsChanged;
        if (Detail is { } detail)
        {
            detail.UndoExpired -= OnDetailUndoExpired;
            detail.Dispose();
            Detail = null;
        }
    }
}
