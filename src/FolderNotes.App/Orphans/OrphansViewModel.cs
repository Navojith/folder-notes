using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using FolderNotes.App.Library;
using FolderNotes.App.Services;
using FolderNotes.Core.Domain;
using FolderNotes.Core.Identity;
using FolderNotes.Core.Storage;
using Serilog;

namespace FolderNotes.App.Orphans;

/// <summary>Why a note is on the Orphaned page (05 § Orphaned page).</summary>
public enum OrphanReason
{
    NotFound,
    InRecycleBin,
    DriveUnavailable,

    /// <summary>The folder is somewhere else, but another note is attached there.</summary>
    Conflict,

    /// <summary>The folder was found again; the next check reattaches the note.</summary>
    FoundAgain,
}

/// <summary>One orphaned note (05 § Orphaned page).</summary>
public sealed partial class OrphanItem : ObservableObject
{
    public const int MaxChips = LibraryRow.MaxChips;

    private readonly CultureInfo _culture;
    private readonly TimeZoneInfo _zone;

    internal OrphanItem(Note note, FolderCheck check, CultureInfo culture, TimeZoneInfo zone)
    {
        _culture = culture;
        _zone = zone;
        Note = note;
        Check = check;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(PathDisplay), nameof(Status), nameof(AccentColor), nameof(HasAccent),
        nameof(Chips), nameof(OrphanedText), nameof(AccessibleName))]
    public partial Note Note { get; internal set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Reason), nameof(ReasonText), nameof(AccessibleName))]
    public partial FolderCheck Check { get; internal set; }

    /// <summary>The last relink error for this row, or null (05: nothing changed).</summary>
    [ObservableProperty]
    public partial string? Error { get; internal set; }

    /// <summary>True while a relink or delete for this row runs (buttons disabled).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    public partial bool IsBusy { get; internal set; }

    public bool IsIdle => !IsBusy;

    public Guid Id => Note.Id;

    public string Title => Note.Title;

    public string PathDisplay => Note.Folder.PathDisplay;

    public NoteStatus Status => Note.Status;

    public string AccentColor => Note.AccentColor ?? "";

    public bool HasAccent => Note.AccentColor is not null;

    public IReadOnlyList<Label> Chips => Note.Labels.Take(MaxChips).ToArray();

    public OrphanReason Reason => Check.State switch
    {
        FolderState.InRecycleBin => OrphanReason.InRecycleBin,
        FolderState.DriveUnavailable => OrphanReason.DriveUnavailable,
        FolderState.Moved or FolderState.Recreated => OrphanReason.Conflict,
        FolderState.Present => OrphanReason.FoundAgain,
        _ => OrphanReason.NotFound,
    };

    public string ReasonText => Reason switch
    {
        OrphanReason.InRecycleBin => "The folder is in the Recycle Bin. Restore it and the note reattaches.",
        OrphanReason.DriveUnavailable => "The drive or network location isn't available. Reconnect it and the note reattaches.",
        OrphanReason.Conflict => $"The folder is now at {Check.Path}, but another note is attached there.",
        OrphanReason.FoundAgain => "The folder was found again. Check again to reattach the note.",
        _ => "The folder was deleted, or moved to another drive.",
    };

    public string OrphanedText => Note.OrphanedAt is { } at
        ? "Orphaned " + LibraryFormat.FormatDate(DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, _zone).DateTime), _culture)
        : "";

    public string AccessibleName => $"{Title}, {PathDisplay}, {ReasonText}";

    public override string ToString() => AccessibleName;
}

/// <summary>
/// The Orphaned page (specs/05 § Orphaned page, 02 § Orphaned page and orphan scan, REQ-061, ADR-020):
/// the list with reasons, per-row checks, Check again, relink with confirm and Undo, delete with
/// Undo, open note. UI-thread only; no XAML types.
/// </summary>
public sealed partial class OrphansViewModel : ObservableObject, IDisposable
{
    public static readonly TimeSpan UndoWindow = TimeSpan.FromSeconds(10);
    internal const string RelinkCommitText = "Relink";

    private readonly INoteRepository _notes;
    private readonly OrphanScanner _scanner;
    private readonly Func<Task<OrphanScanReport>> _scanAll;
    private readonly IFolderIdentityService _identity;
    private readonly INoteEvents _events;
    private readonly IUiDispatcher _ui;
    private readonly TimeProvider _clock;
    private readonly ILogger _log;
    private readonly Func<string, bool> _directoryExists;
    private readonly CultureInfo _culture;
    private readonly TimeZoneInfo _zone;
    private Task _reload = Task.CompletedTask;
    private bool _reloadAgain;
    private bool _loadedOnce;
    private bool _disposed;
    private ITimer? _undoTimer;
    private Func<Task>? _undo;
    private long _barVersion;

    internal OrphansViewModel(
        INoteRepository notes,
        OrphanScanner scanner,
        Func<Task<OrphanScanReport>> scanAll,
        IFolderIdentityService identity,
        INoteEvents events,
        IUiDispatcher ui,
        TimeProvider clock,
        ILogger log,
        Func<string, bool>? directoryExists = null,
        CultureInfo? culture = null,
        TimeZoneInfo? zone = null)
    {
        _notes = notes ?? throw new ArgumentNullException(nameof(notes));
        _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
        _scanAll = scanAll ?? throw new ArgumentNullException(nameof(scanAll));
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _log = (log ?? throw new ArgumentNullException(nameof(log))).ForContext<OrphansViewModel>();
        _directoryExists = directoryExists ?? Directory.Exists;
        _culture = culture ?? CultureInfo.CurrentCulture;
        _zone = zone ?? TimeZoneInfo.Local;
        _events.NoteChanged += OnNoteChanged;
    }

    /// <summary>Set by the main window (it owns the picker dialog).</summary>
    internal IFolderPicker? Picker { get; set; }

    /// <summary>Asks "Relink to a different folder?" (title, message → true = Relink). Set by the page.</summary>
    internal Func<string, string, Task<bool>>? Confirm { get; set; }

    /// <summary>Open note → the library (AppShell.OpenNote).</summary>
    public event EventHandler<Guid>? OpenNoteRequested;

    public ObservableCollection<OrphanItem> Items { get; } = [];

    /// <summary>For the navigation badge (0 = no badge).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText), nameof(IsEmpty), nameof(NavName))]
    public partial int Count { get; private set; }

    public string CountText => Count == 1 ? "1 note" : Count.ToString(_culture) + " notes";

    public bool IsEmpty => _loadedOnce && Count == 0;

    /// <summary>UIA name of the navigation item (05).</summary>
    public string NavName => Count == 0 ? "Orphaned" : "Orphaned, " + CountText;

    /// <summary>The result of the last Check again, or null.</summary>
    [ObservableProperty]
    public partial string? CheckMessage { get; private set; }

    [ObservableProperty]
    public partial bool IsChecking { get; private set; }

    /// <summary>The bar after a relink / delete (with Undo) or a failed undo.</summary>
    [ObservableProperty]
    public partial string? BarText { get; private set; }

    [ObservableProperty]
    public partial bool CanUndo { get; private set; }

    // ───────────── loading ─────────────

    /// <summary>Opening the page: check the listed notes again (05), then list.</summary>
    public async Task ActivateAsync()
    {
        var orphans = await _notes.ListAsync(new NoteQuery { State = NoteListState.Orphaned, Sort = NoteSort.Path });
        foreach (var note in orphans)
        {
            if (_disposed)
            {
                return;
            }

            // Off the UI thread: the file system can block (an offline share).
            var result = await Task.Run(() => _scanner.CheckAsync(note));
            if (result.Outcome is OrphanOutcome.Recovered or OrphanOutcome.Relinked)
            {
                _log.Information("Orphaned page: note {NoteId} reattached ({State})", note.Id, result.Check.State);
            }
        }

        await ReloadAsync();
    }

    /// <summary>Coalesced like the library's.</summary>
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

    internal Task WhenIdleAsync() => _reload;

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
        var orphans = await _notes.ListAsync(new NoteQuery { State = NoteListState.Orphaned, Sort = NoteSort.Path });
        if (_disposed)
        {
            return;
        }

        // Off the UI thread: the file system can block (an offline share).
        var checks = await Task.Run(() => orphans.Select(_scanner.Classify).ToArray());
        if (_disposed)
        {
            return;
        }

        var byId = Items.ToDictionary(i => i.Id);
        var next = new List<OrphanItem>(orphans.Count);
        for (var i = 0; i < orphans.Count; i++)
        {
            var note = orphans[i];
            var check = checks[i];
            if (byId.TryGetValue(note.Id, out var item))
            {
                item.Note = note; // keeps the row's error and busy state
                item.Check = check;
            }
            else
            {
                item = new OrphanItem(note, check, _culture, _zone);
            }

            next.Add(item);
        }

        if (!Items.SequenceEqual(next))
        {
            Items.Clear();
            foreach (var item in next)
            {
                Items.Add(item);
            }
        }

        _loadedOnce = true;
        Count = orphans.Count;
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>Check again: the full scan over every note (05).</summary>
    public async Task CheckAgainAsync()
    {
        if (IsChecking)
        {
            return;
        }

        IsChecking = true;
        try
        {
            var report = await _scanAll();
            CheckMessage = FormatReport(report);
        }
#pragma warning disable CA1031 // Shown on the page; the scheduled scan retries.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log.Error(ex, "Check again failed");
            CheckMessage = "Couldn't check the folders: " + ex.Message;
        }
        finally
        {
            IsChecking = false;
        }

        await ReloadAsync();
    }

    public void ClearCheckMessage() => CheckMessage = null;

    internal static string FormatReport(OrphanScanReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var parts = new List<string>();
        var reattached = report.Relinked + report.Recovered;
        if (reattached > 0)
        {
            parts.Add(reattached.ToString(CultureInfo.CurrentCulture) + " reattached");
        }

        if (report.Orphaned > 0)
        {
            parts.Add(report.Orphaned.ToString(CultureInfo.CurrentCulture) + " newly orphaned");
        }

        if (report.Conflicts > 0)
        {
            parts.Add(report.Conflicts.ToString(CultureInfo.CurrentCulture) + (report.Conflicts == 1 ? " conflict" : " conflicts"));
        }

        var checkedText = "Checked " + (report.Checked == 1 ? "1 note." : report.Checked.ToString(CultureInfo.CurrentCulture) + " notes.");
        return parts.Count == 0 ? checkedText + " Nothing changed." : checkedText + " " + string.Join(", ", parts) + ".";
    }

    // ───────────── relink (03 § Relink rules) ─────────────

    /// <summary>Relink…: pick, check, maybe confirm, write conditionally, offer Undo (05).</summary>
    public async Task RelinkAsync(OrphanItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.IsBusy || Picker is not { } picker)
        {
            return;
        }

        item.IsBusy = true;
        item.Error = null;
        try
        {
            await RelinkCoreAsync(item, picker);
        }
        finally
        {
            item.IsBusy = false;
        }
    }

    private async Task RelinkCoreAsync(OrphanItem item, IFolderPicker picker)
    {
        var note = item.Note;
        string? picked;
        try
        {
            picked = await picker.PickFolderAsync(NearestExistingFolder(note.Folder.PathDisplay), RelinkCommitText);
        }
#pragma warning disable CA1031 // Shown under the row.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log.Error(ex, "The folder picker failed");
            item.Error = "Couldn't open the folder picker: " + ex.Message;
            return;
        }

        if (picked is null)
        {
            return; // cancelled
        }

        var check = RelinkTarget.Check(_identity, note, picked, _directoryExists);
        if (check.Problem != RelinkProblem.None || check.Target is not { } target)
        {
            item.Error = check.Problem == RelinkProblem.InRecycleBin
                ? "That folder is in the Recycle Bin. Restore it first."
                : "That isn't a folder.";
            return;
        }

        if (check.NameDiffers)
        {
            var (title, message) = DifferentNameConfirmation(note, target);
            if (Confirm is null || !await Confirm(title, message))
            {
                return;
            }
        }

        var previous = note.Folder;
        try
        {
            await _notes.RelinkAsync(note.Id, target, previous);
        }
        catch (DuplicateFolderNoteException ex)
        {
            var other = await _notes.GetByIdAsync(ex.ExistingNoteId);
            item.Error = other is null
                ? $"{target.PathDisplay} already has a note. Delete or relink that note first."
                : $"{target.PathDisplay} already has a note ('{other.Title}'). Delete or relink that note first.";
            return;
        }
        catch (Exception ex) when (ex is NoteBindingChangedException or KeyNotFoundException)
        {
            item.Error = "This note changed in the meantime.";
            await ReloadAsync();
            return;
        }
#pragma warning disable CA1031 // Shown under the row; the write was one transaction, so nothing changed.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log.Error(ex, "Relinking note {NoteId} failed", note.Id);
            item.Error = "Couldn't relink: " + ex.Message;
            return;
        }

        _log.Information("Relinked note {NoteId} from {Old} to {New}", note.Id, previous.PathDisplay, target.PathDisplay);
        ShowBar($"Relinked '{note.Title}' to {target.PathDisplay}.", async () =>
        {
            await _notes.RelinkAsync(note.Id, previous, target);
            await _notes.SetOrphanedAsync(note.Id, true, previous);
            _log.Information("Undid the relink of note {NoteId} (back to {Old})", note.Id, previous.PathDisplay);
        });
        await ReloadAsync();
    }

    internal static (string Title, string Message) DifferentNameConfirmation(Note note, FolderRef target)
    {
        ArgumentNullException.ThrowIfNull(note);
        ArgumentNullException.ThrowIfNull(target);
        return ("Relink to a different folder?",
            $"'{note.Title}' was for '{note.Folder.FolderName}'. You picked '{target.FolderName}' ({target.PathDisplay}).");
    }

    /// <summary>The stored path, or its nearest parent that exists (the picker's start folder).</summary>
    internal string? NearestExistingFolder(string path)
    {
        for (var p = path; !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p))
        {
            if (_directoryExists(p))
            {
                return p;
            }
        }

        return null;
    }

    // ───────────── delete / open ─────────────

    /// <summary>Soft delete with Undo (REQ-006, 05).</summary>
    public async Task DeleteAsync(OrphanItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.IsBusy)
        {
            return;
        }

        item.IsBusy = true;
        item.Error = null;
        var note = item.Note;
        try
        {
            await _notes.DeleteAsync(note.Id);
        }
        catch (KeyNotFoundException)
        {
            // already gone; the reload shows it
        }
#pragma warning disable CA1031 // Shown under the row.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log.Error(ex, "Deleting orphaned note {NoteId} failed", note.Id);
            item.Error = "Couldn't delete: " + ex.Message;
            return;
        }
        finally
        {
            item.IsBusy = false;
        }

        _log.Information("Deleted orphaned note {NoteId}; undo offered", note.Id);
        ShowBar($"Deleted '{note.Title}'.", () => _notes.RestoreAsync(note.Id));
        await ReloadAsync();
    }

    public void OpenNote(OrphanItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        OpenNoteRequested?.Invoke(this, item.Id);
    }

    // ───────────── the undo bar ─────────────

    public async Task UndoAsync()
    {
        if (_undo is not { } undo)
        {
            return;
        }

        CloseBar();
        try
        {
            await undo();
        }
        catch (DuplicateFolderNoteException)
        {
            BarText = "Couldn't undo: that folder has another note now.";
        }
#pragma warning disable CA1031 // Shown in the bar.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log.Error(ex, "Undo on the Orphaned page failed");
            BarText = "Couldn't undo: " + ex.Message;
        }

        await ReloadAsync();
    }

    public void CloseBar()
    {
        _undoTimer?.Dispose();
        _undoTimer = null;
        _undo = null;
        CanUndo = false;
        BarText = null;
        _barVersion++;
    }

    private void ShowBar(string text, Func<Task> undo)
    {
        CloseBar();
        var version = _barVersion;
        BarText = text;
        _undo = undo;
        CanUndo = true;
        _undoTimer = _clock.CreateTimer(_ => _ui.Post(() =>
        {
            if (version == _barVersion && !_disposed)
            {
                CloseBar();
            }
        }), null, UndoWindow, Timeout.InfiniteTimeSpan);
    }

    // ───────────── sync ─────────────

    private async void OnNoteChanged(object? sender, NoteChangedEventArgs e)
    {
        try
        {
            if (!_disposed)
            {
                await ReloadAsync(); // also keeps the badge right before the page is first opened
            }
        }
#pragma warning disable CA1031 // An event handler must not crash the UI thread; the next reload repairs the page.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log.Error(ex, "Reloading the Orphaned page failed");
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _undoTimer?.Dispose();
        _events.NoteChanged -= OnNoteChanged;
    }
}
