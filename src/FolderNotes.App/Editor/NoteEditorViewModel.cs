using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FolderNotes.App.Services;
using FolderNotes.Core.Domain;
using FolderNotes.Core.Identity;
using FolderNotes.Core.Storage;
using Serilog;

namespace FolderNotes.App.Editor;

/// <summary>Where the editing session is (specs/05 § Editor behaviour).</summary>
public enum EditorState
{
    Loading,
    Editing,

    /// <summary>Soft-deleted; Undo is offered for <see cref="NoteEditorViewModel.UndoWindow"/>.</summary>
    Deleted,

    /// <summary>The folder or note doesn't exist; <see cref="NoteEditorViewModel.MissingMessage"/> says which.</summary>
    Missing,
}

/// <summary>The autosave indicator (REQ-005).</summary>
public enum SaveState
{
    None,
    Pending,
    Saving,
    Saved,
    Invalid,
    Failed,
}

/// <summary>One label suggestion: an existing label, or "Create label …" (REQ-011).</summary>
public sealed record LabelSuggestion(string Text, Label? Existing, string? CreateName)
{
    public override string ToString() => Text;
}

/// <summary>
/// One editing session for one note (REQ-002…007, 011, 020; ADR-014). UI-thread only: every public
/// member and every continuation runs on the UI thread; timers are marshalled through <see cref="IUiDispatcher"/>.
/// </summary>
public sealed partial class NoteEditorViewModel : ObservableObject, IDisposable
{
    public static readonly TimeSpan AutosaveDelay = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan UndoWindow = TimeSpan.FromSeconds(10);
    public const int MaxSuggestions = 8;

    private readonly INoteRepository _notes;
    private readonly ILabelRepository _labelStore;
    private readonly IFolderIdentityService _identity;
    private readonly TimeProvider _clock;
    private readonly IUiDispatcher _ui;
    private readonly ILogger _log;
    private readonly Func<string, bool> _directoryExists;

    private Note? _saved;          // last stored version; null while this is an unsaved draft
    private Note? _draftBase;      // the draft's starting point (REQ-003)
    private IReadOnlyList<Label> _allLabels = [];
    private bool _applying;        // true while fields are set from a note, not by the user
    private bool _dirty;
    private long _editVersion;
    private bool _saveAgain;
    private Task _inflight = Task.CompletedTask;
    private ITimer? _saveTimer;
    private ITimer? _undoTimer;
    private Guid? _deletedId;
    private bool _disposed;

    internal NoteEditorViewModel(
        INoteRepository notes,
        ILabelRepository labels,
        IFolderIdentityService identity,
        TimeProvider clock,
        IUiDispatcher ui,
        ILogger log,
        Func<string, bool>? directoryExists = null)
    {
        _notes = notes ?? throw new ArgumentNullException(nameof(notes));
        _labelStore = labels ?? throw new ArgumentNullException(nameof(labels));
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _log = (log ?? throw new ArgumentNullException(nameof(log))).ForContext<NoteEditorViewModel>();
        _directoryExists = directoryExists ?? Directory.Exists;
        Labels.CollectionChanged += (_, _) => OnEdited();
    }

    /// <summary>Raised when the undo window after a delete runs out (the compact window closes itself).</summary>
    public event EventHandler? UndoExpired;

    // ───────────── session state ─────────────

    [ObservableProperty]
    public partial EditorState State { get; private set; } = EditorState.Loading;

    [ObservableProperty]
    public partial string? MissingMessage { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveText))]
    public partial SaveState SaveState { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveText))]
    public partial string? SaveError { get; private set; }

    [ObservableProperty]
    public partial string FolderPath { get; private set; } = "";

    [ObservableProperty]
    public partial bool FolderExists { get; private set; }

    /// <summary>Not stored yet (05: drafts are saved on the first edit).</summary>
    public bool IsDraft => _saved is null;

    public bool CanDelete => !IsDraft && State == EditorState.Editing;

    public Guid? NoteId => _saved?.Id ?? _draftBase?.Id;

    /// <summary><c>path_norm</c> of the note's folder, for de-duplicating editor windows.</summary>
    public string? FolderKey => (_saved ?? _draftBase)?.Folder.PathNorm;

    public string SaveText => SaveState switch
    {
        SaveState.Pending or SaveState.Saving => "Saving…",
        SaveState.Saved => "Saved",
        SaveState.Invalid => "Not saved: " + FirstError(),
        SaveState.Failed => "Couldn't save: " + SaveError,
        _ => "",
    };

    // ───────────── fields ─────────────

    [ObservableProperty]
    public partial string Title { get; set; } = "";

    [ObservableProperty]
    public partial string Body { get; set; } = "";

    [ObservableProperty]
    public partial NoteStatus Status { get; set; }

    /// <summary>A palette key, or null for no accent.</summary>
    [ObservableProperty]
    public partial string? AccentColor { get; set; }

    [ObservableProperty]
    public partial DateOnly? KeepUntil { get; set; }

    [ObservableProperty]
    public partial DateOnly? ReviewOn { get; set; }

    [ObservableProperty]
    public partial DateOnly? SafeAfter { get; set; }

    public ObservableCollection<Label> Labels { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveText))]
    public partial string? TitleError { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveText))]
    public partial string? BodyError { get; private set; }

    /// <summary>The last label that couldn't be added, or a label rule broken by the note.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveText))]
    public partial string? LabelError { get; private set; }

    // ───────────── loading ─────────────

    /// <summary><c>--edit &lt;path&gt;</c>: the existing note for the folder, or a draft (REQ-001, 003, 004).</summary>
    public async Task LoadForFolderAsync(string path, CancellationToken ct = default)
    {
        State = EditorState.Loading;
        FolderPath = path ?? "";
        if (string.IsNullOrWhiteSpace(path) || !_identity.TryResolve(path, out var folder) || !_directoryExists(folder.PathDisplay))
        {
            SetMissing("This folder doesn't exist.");
            return;
        }

        var note = await _notes.GetByFolderAsync(folder, ct);
        _allLabels = await _labelStore.ListLabelsAsync(ct);
        if (note is null)
        {
            _draftBase = Note.CreateFor(folder, _clock);
            _saved = null;
            Apply(_draftBase);
            _log.Information("Editing a new draft for {Folder}", folder.PathDisplay);
        }
        else
        {
            _saved = note;
            Apply(note);
            _log.Information("Editing note {NoteId} for {Folder}", note.Id, folder.PathDisplay);
        }

        FolderExists = true;
        State = EditorState.Editing;
        RaiseIdentityChanged();
    }

    /// <summary><c>--open-note &lt;id&gt;</c>.</summary>
    public async Task LoadByIdAsync(Guid id, CancellationToken ct = default)
    {
        State = EditorState.Loading;
        var note = await _notes.GetByIdAsync(id, ct);
        if (note is null || note.IsDeleted)
        {
            SetMissing("This note no longer exists.");
            return;
        }

        _allLabels = await _labelStore.ListLabelsAsync(ct);
        _saved = note;
        Apply(note);
        FolderExists = _directoryExists(note.Folder.PathDisplay);
        State = EditorState.Editing;
        RaiseIdentityChanged();
    }

    // ───────────── labels (REQ-011) ─────────────

    public IReadOnlyList<LabelSuggestion> SuggestLabels(string? text)
    {
        var t = (text ?? "").Trim();
        var available = _allLabels.Where(l => !Labels.Any(on => Label.NameComparer.Equals(on.Name, l.Name))).ToList();
        IEnumerable<Label> matches = t.Length == 0
            ? available
            : available.Where(l => l.Name.StartsWith(t, StringComparison.OrdinalIgnoreCase))
                .Concat(available.Where(l => !l.Name.StartsWith(t, StringComparison.OrdinalIgnoreCase)
                                          && l.Name.Contains(t, StringComparison.OrdinalIgnoreCase)));
        var result = matches.Take(MaxSuggestions).Select(l => new LabelSuggestion(l.Name, l, null)).ToList();

        var exists = _allLabels.Concat(Labels).Any(l => Label.NameComparer.Equals(l.Name, t));
        if (t.Length > 0 && !exists)
        {
            result.Add(new LabelSuggestion($"Create label \"{t}\"", null, t));
        }

        return result;
    }

    public bool AddLabel(LabelSuggestion suggestion)
    {
        ArgumentNullException.ThrowIfNull(suggestion);
        return suggestion.Existing is { } existing ? TryAdd(existing) : AddLabel(suggestion.CreateName ?? "");
    }

    /// <summary>Adds an existing label by name, or a new one with the least-used palette colour.</summary>
    public bool AddLabel(string? text)
    {
        var name = (text ?? "").Trim();
        var existing = _allLabels.FirstOrDefault(l => Label.NameComparer.Equals(l.Name, name));
        return TryAdd(existing ?? Label.Create(name, NextLabelColor()));
    }

    public void RemoveLabel(Label label)
    {
        var match = Labels.FirstOrDefault(l => l.Id == label?.Id);
        if (match is not null)
        {
            LabelError = null;
            Labels.Remove(match);
        }
    }

    /// <summary>
    /// Labels changed elsewhere (ADR-018): show current names and colours, and drop labels that no
    /// longer exist, without counting as an edit, so an autosave can't re-create a deleted label.
    /// </summary>
    public async Task RefreshLabelsAsync()
    {
        if (_disposed)
        {
            return;
        }

        var all = await _labelStore.ListLabelsAsync();
        if (_disposed)
        {
            return;
        }

        var previous = _allLabels;
        _allLabels = all;
        var byId = all.ToDictionary(l => l.Id);
        var wasApplying = _applying;
        _applying = true;
        try
        {
            for (var i = Labels.Count - 1; i >= 0; i--)
            {
                var label = Labels[i];
                if (!byId.TryGetValue(label.Id, out var current))
                {
                    // Gone from the store: drop it. A label created inline in this editor and not
                    // saved yet was never stored, so it stays.
                    if (previous.Any(l => l.Id == label.Id) || _saved?.Labels.Any(l => l.Id == label.Id) == true)
                    {
                        Labels.RemoveAt(i);
                    }
                }
                else if (current != label)
                {
                    Labels[i] = current;
                }
            }

            if (_saved is { } saved)
            {
                // The store already applied the change to the note; keep the baseline in step.
                _saved = saved with { Labels = [.. saved.Labels.Where(l => byId.ContainsKey(l.Id)).Select(l => byId[l.Id])] };
            }
        }
        finally
        {
            _applying = wasApplying;
        }
    }

    /// <summary>The palette colour used by the fewest labels; ties go to palette order (05).</summary>
    public string NextLabelColor()
    {
        var labels = _allLabels.Concat(Labels.Where(l => !_allLabels.Any(a => a.Id == l.Id)));
        return Palette.LeastUsed(labels.Select(l => l.Color));
    }

    private bool TryAdd(Label label)
    {
        var errors = NoteValidator.ValidateLabel(label).ToList();
        if (Labels.Any(l => Label.NameComparer.Equals(l.Name, label.Name)))
        {
            errors.Add(new ValidationError("labels.duplicate", "labels", $"The label '{label.Name}' is already on this note."));
        }

        if (errors.Count > 0)
        {
            LabelError = errors[0].Message;
            return false;
        }

        LabelError = null;
        Labels.Add(label);
        return true;
    }

    // ───────────── saving (REQ-005) ─────────────

    /// <summary><c>Ctrl+Enter</c>: an explicit save, which also creates an untouched draft.</summary>
    public Task SaveNowAsync() => FlushCoreAsync(force: true);

    /// <summary>Close / <c>Esc</c>: run a pending save now; invalid fields fall back to their last saved value.</summary>
    public Task FlushAsync() => FlushCoreAsync(force: false);

    /// <summary>Completes when no save is running or scheduled. For tests and shutdown.</summary>
    public async Task WhenIdleAsync()
    {
        while (!_inflight.IsCompleted)
        {
            await _inflight;
        }
    }

    private async Task FlushCoreAsync(bool force)
    {
        CancelSaveTimer();
        if (State != EditorState.Editing)
        {
            return;
        }

        if (force)
        {
            _dirty = true;
        }

        if (!_dirty)
        {
            await WhenIdleAsync();
            return;
        }

        RepairInvalidFields();
        await StartSave();
        await WhenIdleAsync();
    }

    private void OnEdited()
    {
        if (_applying || State != EditorState.Editing)
        {
            return;
        }

        _dirty = true;
        _editVersion++;
        if (Validate())
        {
            SaveState = SaveState.Pending;
            ScheduleSave();
        }
        else
        {
            CancelSaveTimer();
            SaveState = SaveState.Invalid;
        }
    }

    private void ScheduleSave()
    {
        _saveTimer?.Dispose();
        _saveTimer = _clock.CreateTimer(_ => _ui.Post(OnSaveTimer), null, AutosaveDelay, Timeout.InfiniteTimeSpan);
    }

    private void OnSaveTimer()
    {
        if (_disposed || State != EditorState.Editing)
        {
            return;
        }

        _saveTimer?.Dispose();
        _saveTimer = null;
        _ = StartSave();
    }

    private void CancelSaveTimer()
    {
        _saveTimer?.Dispose();
        _saveTimer = null;
    }

    private Task StartSave()
    {
        if (!_inflight.IsCompleted)
        {
            _saveAgain = true; // one save at a time; the running loop picks this up
            return _inflight;
        }

        _inflight = SaveLoopAsync();
        return _inflight;
    }

    private async Task SaveLoopAsync()
    {
        do
        {
            _saveAgain = false;
            if (!_dirty || State != EditorState.Editing)
            {
                return;
            }

            var version = _editVersion;
            var note = BuildNote();
            if (NoteValidator.Validate(note).Count > 0)
            {
                SaveState = SaveState.Invalid;
                return;
            }

            SaveState = SaveState.Saving;
            try
            {
                var wasDraft = IsDraft;
                var stored = await _notes.UpsertAsync(note);
                _saved = stored;
                SaveError = null;
                if (version == _editVersion)
                {
                    _dirty = false;
                    SaveState = SaveState.Saved;
                }
                else if (_saveTimer is null)
                {
                    _saveAgain = true;
                }
                else
                {
                    SaveState = SaveState.Pending;
                }

                if (stored.Labels.Any(l => !_allLabels.Any(a => a.Id == l.Id)))
                {
                    _allLabels = await _labelStore.ListLabelsAsync();
                }

                if (wasDraft)
                {
                    _log.Information("Created note {NoteId} for {Folder}", stored.Id, stored.Folder.PathDisplay);
                    RaiseIdentityChanged();
                }
            }
#pragma warning disable CA1031 // Shown in the indicator; the next edit retries (05 § Editor behaviour).
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _log.Error(ex, "Saving note {NoteId} failed", note.Id);
                SaveError = ex is DuplicateFolderNoteException ? "this folder already has another note." : ex.Message;
                SaveState = SaveState.Failed;
                return;
            }
        }
        while (_saveAgain);
    }

    private Note BuildNote() => (_saved ?? _draftBase ?? throw new InvalidOperationException("Nothing loaded.")) with
    {
        Title = Title,
        BodyMd = Body,
        Status = Status,
        AccentColor = AccentColor,
        KeepUntil = KeepUntil,
        ReviewOn = ReviewOn,
        SafeAfter = SafeAfter,
        Labels = Labels.ToArray(),
    };

    /// <summary>Validates the current fields and shows the inline errors. True when the note can be saved.</summary>
    private bool Validate()
    {
        var errors = NoteValidator.Validate(BuildNote());
        TitleError = errors.FirstOrDefault(e => e.Field == "title")?.Message;
        BodyError = errors.FirstOrDefault(e => e.Field == "body")?.Message;
        var labelError = errors.FirstOrDefault(e => e.Field.StartsWith("label", StringComparison.Ordinal))?.Message;
        if (labelError is not null)
        {
            LabelError = labelError;
        }

        return errors.Count == 0;
    }

    private string? FirstError() => TitleError ?? BodyError ?? LabelError;

    private void RepairInvalidFields()
    {
        if (Validate())
        {
            return;
        }

        var fallback = _saved ?? _draftBase!;
        _applying = true;
        try
        {
            if (TitleError is not null)
            {
                _log.Information("Discarding an invalid title on close ({Error})", TitleError);
                Title = fallback.Title;
            }

            if (BodyError is not null)
            {
                _log.Information("Discarding an invalid body on close ({Error})", BodyError);
                Body = fallback.BodyMd;
            }
        }
        finally
        {
            _applying = false;
        }

        Validate();
    }

    // ───────────── delete / undo (REQ-006) ─────────────

    /// <summary>Soft-deletes the note (after the view confirmed). Drops any pending save.</summary>
    public async Task DeleteAsync()
    {
        if (!CanDelete)
        {
            return;
        }

        CancelSaveTimer();
        _dirty = false;
        await WhenIdleAsync();
        var id = _saved!.Id;
        try
        {
            await _notes.DeleteAsync(id);
        }
#pragma warning disable CA1031 // Shown in the indicator.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log.Error(ex, "Deleting note {NoteId} failed", id);
            SaveError = ex.Message;
            SaveState = SaveState.Failed;
            return;
        }

        _deletedId = id;
        State = EditorState.Deleted;
        SaveState = SaveState.None;
        OnPropertyChanged(nameof(CanDelete));
        _undoTimer?.Dispose();
        _undoTimer = _clock.CreateTimer(_ => _ui.Post(OnUndoTimer), null, UndoWindow, Timeout.InfiniteTimeSpan);
        _log.Information("Deleted note {NoteId}; undo offered for {Seconds} s", id, UndoWindow.TotalSeconds);
    }

    public async Task UndoDeleteAsync()
    {
        if (State != EditorState.Deleted || _deletedId is not { } id)
        {
            return;
        }

        _undoTimer?.Dispose();
        _undoTimer = null;
        try
        {
            await _notes.RestoreAsync(id);
            var note = await _notes.GetByIdAsync(id) ?? throw new InvalidOperationException("The restored note is missing.");
            _deletedId = null;
            _saved = note;
            Apply(note);
            SaveState = SaveState.None;
            State = EditorState.Editing;
            OnPropertyChanged(nameof(CanDelete));
            _log.Information("Restored note {NoteId}", id);
        }
#pragma warning disable CA1031 // Shown in the indicator.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log.Error(ex, "Restoring note {NoteId} failed", id);
            SaveError = ex is DuplicateFolderNoteException ? "this folder has a new note now." : ex.Message;
            SaveState = SaveState.Failed;
        }
    }

    private void OnUndoTimer()
    {
        _undoTimer?.Dispose();
        _undoTimer = null;
        if (!_disposed && State == EditorState.Deleted)
        {
            UndoExpired?.Invoke(this, EventArgs.Empty);
        }
    }

    // ───────────── helpers ─────────────

    private void Apply(Note note)
    {
        _applying = true;
        try
        {
            Title = note.Title;
            Body = note.BodyMd;
            Status = note.Status;
            AccentColor = note.AccentColor;
            KeepUntil = note.KeepUntil;
            ReviewOn = note.ReviewOn;
            SafeAfter = note.SafeAfter;
            Labels.Clear();
            foreach (var label in note.Labels)
            {
                Labels.Add(label);
            }

            FolderPath = note.Folder.PathDisplay;
            TitleError = BodyError = LabelError = null;
            _dirty = false;
        }
        finally
        {
            _applying = false;
        }
    }

    private void SetMissing(string message)
    {
        MissingMessage = message;
        State = EditorState.Missing;
        _log.Information("Editor: {Message} ({Path})", message, FolderPath);
    }

    private void RaiseIdentityChanged()
    {
        OnPropertyChanged(nameof(IsDraft));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(NoteId));
        OnPropertyChanged(nameof(FolderKey));
    }

    partial void OnTitleChanged(string value) => OnEdited();

    partial void OnBodyChanged(string value) => OnEdited();

    partial void OnStatusChanged(NoteStatus value) => OnEdited();

    partial void OnAccentColorChanged(string? value) => OnEdited();

    partial void OnKeepUntilChanged(DateOnly? value) => OnEdited();

    partial void OnReviewOnChanged(DateOnly? value) => OnEdited();

    partial void OnSafeAfterChanged(DateOnly? value) => OnEdited();

    public void Dispose()
    {
        _disposed = true;
        CancelSaveTimer();
        _undoTimer?.Dispose();
        _undoTimer = null;
    }
}
