using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using FolderNotes.App.Library;
using FolderNotes.App.Services;
using FolderNotes.Core.Search;
using FolderNotes.Core.Storage;
using Serilog;

namespace FolderNotes.App.Search;

/// <summary>One hit in the palette: the library's row strings plus highlights (05 § Search palette behaviour).</summary>
public sealed class SearchResultRow
{
    public const int PathChars = 60;

    internal SearchResultRow(LibraryRow row, SearchHit hit, IReadOnlyList<SearchTerm> terms)
    {
        Row = row;
        Kind = hit.Kind;
        PathText = LibraryFormat.TrimPathMiddle(row.PathDisplay, PathChars);
        var highlight = hit.Kind == SearchMatchKind.FullText ? terms : [];
        TitleRanges = SearchHighlight.Ranges(row.Title, highlight);
        PathRanges = SearchHighlight.Ranges(PathText, highlight);
    }

    public LibraryRow Row { get; }

    public SearchMatchKind Kind { get; }

    public Guid Id => Row.Id;

    public string PathText { get; }

    /// <summary>"path · next date[ · Folder not found]".</summary>
    public string Details => string.Join(" · ", new[] { PathText, Row.NextDate, Row.FolderMissingText }.Where(s => s.Length > 0));

    public IReadOnlyList<HighlightRange> TitleRanges { get; }

    public IReadOnlyList<HighlightRange> PathRanges { get; }

    public override string ToString() => Row.AccessibleName;
}

/// <summary>What Enter did.</summary>
public enum OpenFolderResult
{
    Opened,
    FolderMissing,
    NoSelection,
}

/// <summary>
/// The search palette (specs/05 § Search palette behaviour, 02 § Search palette, REQ-050…053).
/// UI-thread only; no XAML types.
/// </summary>
public sealed partial class SearchPaletteViewModel : ObservableObject, IDisposable
{
    public static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(120);
    public static readonly TimeSpan MessageDuration = TimeSpan.FromSeconds(2);
    public const int MaxResults = 20;
    public const int RecentCount = 8;
    public const string Hints = "↵ open folder · Ctrl+↵ open note · Ctrl+C copy path";

    private readonly ISearchService _search;
    private readonly ILabelRepository _labels;
    private readonly IUiDispatcher _ui;
    private readonly TimeProvider _clock;
    private readonly ILogger _log;
    private readonly Func<string, bool> _openFolder;
    private readonly TimeZoneInfo _zone;
    private readonly CultureInfo _culture;
    private IReadOnlyList<string> _labelNames = [];
    private ITimer? _debounce;
    private ITimer? _messageTimer;
    private CancellationTokenSource? _cts;
    private long _version;
    private Task _inflight = Task.CompletedTask;
    private bool _disposed;

    internal SearchPaletteViewModel(
        ISearchService search,
        ILabelRepository labels,
        IUiDispatcher ui,
        TimeProvider clock,
        ILogger log,
        Func<string, bool>? openFolder = null,
        TimeZoneInfo? zone = null,
        CultureInfo? culture = null)
    {
        _search = search ?? throw new ArgumentNullException(nameof(search));
        _labels = labels ?? throw new ArgumentNullException(nameof(labels));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _log = (log ?? throw new ArgumentNullException(nameof(log))).ForContext<SearchPaletteViewModel>();
        _openFolder = openFolder ?? FolderLauncher.Open;
        _zone = zone ?? TimeZoneInfo.Local;
        _culture = culture ?? CultureInfo.CurrentCulture;
    }

    /// <summary>Ctrl+Enter: open this note in the library (05).</summary>
    public event EventHandler<Guid>? OpenNoteRequested;

    [ObservableProperty]
    public partial string Query { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Selected))]
    public partial IReadOnlyList<SearchResultRow> Results { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Selected))]
    public partial int SelectedIndex { get; private set; } = -1;

    public SearchResultRow? Selected => SelectedIndex >= 0 && SelectedIndex < Results.Count ? Results[SelectedIndex] : null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSuggestions))]
    public partial IReadOnlyList<SearchSuggestion> Suggestions { get; private set; } = [];

    [ObservableProperty]
    public partial int SuggestionIndex { get; private set; } = -1;

    public bool ShowSuggestions => Suggestions.Count > 0;

    /// <summary>A parse error to show under the box, or null.</summary>
    [ObservableProperty]
    public partial string? ErrorText { get; private set; }

    /// <summary>"No notes match", or null.</summary>
    [ObservableProperty]
    public partial string? EmptyText { get; private set; }

    [ObservableProperty]
    public partial string CountText { get; private set; } = "";

    /// <summary>The hints, or a short message ("Path copied") for <see cref="MessageDuration"/>.</summary>
    [ObservableProperty]
    public partial string FooterText { get; private set; } = Hints;

    // ───────────── opening + typing ─────────────

    /// <summary>The palette is shown (05: an empty box lists recent notes; <c>--search</c> prefills).</summary>
    public async Task OpenAsync(string query)
    {
        CancelDebounce();
        ShowMessage(null);
        Suggestions = [];
        Query = query ?? "";
        try
        {
            _labelNames = [.. (await _labels.ListLabelsAsync()).Select(l => l.Name)];
        }
#pragma warning disable CA1031 // Autocomplete is optional; the search still works.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log.Warning(ex, "Loading label names for autocomplete failed");
        }

        await RunSearchAsync();
    }

    /// <summary>The box changed: refresh the suggestions now and search after the debounce.</summary>
    public void SetQuery(string text, int caret)
    {
        text ??= "";
        UpdateSuggestions(text, caret);
        if (string.Equals(text, Query, StringComparison.Ordinal))
        {
            return;
        }

        Query = text;
        CancelDebounce();
        _debounce = _clock.CreateTimer(_ => _ui.Post(() => _ = RunSearchAsync()), null, DebounceDelay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>The caret moved without a text change (suggestions follow the caret).</summary>
    public void SetCaret(int caret) => UpdateSuggestions(Query, caret);

    /// <summary>Completes when no search is pending or running (tests).</summary>
    internal Task WhenIdleAsync() => _inflight;

    private void UpdateSuggestions(string text, int caret)
    {
        var next = SearchCompletion.Suggest(text, caret, _labelNames);
        if (!next.SequenceEqual(Suggestions))
        {
            Suggestions = next;
            SuggestionIndex = next.Count > 0 ? 0 : -1;
        }
    }

    private Task RunSearchAsync()
    {
        _inflight = RunSearchCoreAsync();
        return _inflight;
    }

    private async Task RunSearchCoreAsync()
    {
        if (_disposed)
        {
            return;
        }

        var version = ++_version;
        _cts?.Cancel();
        _cts?.Dispose();
        var cts = _cts = new CancellationTokenSource();
        var parsed = _search.Parse(Query);
        IReadOnlyList<SearchHit> hits;
        try
        {
            hits = await _search.SearchAsync(parsed, Query.Trim().Length == 0 ? RecentCount : MaxResults, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return; // a newer search replaced this one
        }
#pragma warning disable CA1031 // Shown in the palette; the next keystroke retries.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log.Error(ex, "Search failed for {Query}", Query);
            if (version == _version)
            {
                ErrorText = "Search failed: " + ex.Message;
            }

            return;
        }

        if (version != _version || _disposed)
        {
            return; // stale
        }

        Results = [.. hits.Select(h => new SearchResultRow(new LibraryRow(h.Note, _clock, _zone, _culture), h, parsed.Terms))];
        SelectedIndex = Results.Count > 0 ? 0 : -1;
        ErrorText = parsed.Errors.Count > 0 ? parsed.Errors[0].Message : null;
        EmptyText = Results.Count == 0 ? "No notes match" : null;
        CountText = Results.Count == 1 ? "1 result" : Results.Count.ToString(_culture) + " results";
    }

    // ───────────── selection + suggestions ─────────────

    /// <summary>Up / Down: over the suggestions while they show, otherwise the results. Wraps.</summary>
    public void MoveSelection(int delta)
    {
        if (ShowSuggestions)
        {
            SuggestionIndex = Wrap(SuggestionIndex + delta, Suggestions.Count);
        }
        else if (Results.Count > 0)
        {
            SelectedIndex = Wrap(SelectedIndex + delta, Results.Count);
        }
    }

    public void Select(int index)
    {
        if (index >= 0 && index < Results.Count)
        {
            SelectedIndex = index;
        }
    }

    /// <summary>Tab / Enter while suggestions show: the new text and caret, or null.</summary>
    public SearchSuggestion? AcceptSuggestion(int? index = null)
    {
        var i = index ?? SuggestionIndex;
        if (i < 0 || i >= Suggestions.Count)
        {
            return null;
        }

        var chosen = Suggestions[i];
        Suggestions = [];
        SuggestionIndex = -1;
        SetQuery(chosen.NewText, chosen.NewCaret);
        return chosen;
    }

    /// <summary>Esc with suggestions showing closes them first (05). True when it did.</summary>
    public bool DismissSuggestions()
    {
        if (!ShowSuggestions)
        {
            return false;
        }

        Suggestions = [];
        SuggestionIndex = -1;
        return true;
    }

    private static int Wrap(int i, int count) => ((i % count) + count) % count;

    // ───────────── actions (REQ-053) ─────────────

    /// <summary>Enter: open the selected note's folder in Explorer.</summary>
    public OpenFolderResult OpenFolder()
    {
        if (Selected is not { } row)
        {
            return OpenFolderResult.NoSelection;
        }

        if (!_openFolder(row.Row.PathDisplay))
        {
            ShowMessage("Folder not found");
            return OpenFolderResult.FolderMissing;
        }

        return OpenFolderResult.Opened;
    }

    /// <summary>Ctrl+Enter: open the selected note in the library. False with nothing selected.</summary>
    public bool OpenNote()
    {
        if (Selected is not { } row)
        {
            return false;
        }

        OpenNoteRequested?.Invoke(this, row.Id);
        return true;
    }

    /// <summary>Ctrl+C: the selected note's path (the view puts it on the clipboard), or null.</summary>
    public string? CopyPath()
    {
        if (Selected is not { } row)
        {
            return null;
        }

        ShowMessage("Path copied");
        return row.Row.PathDisplay;
    }

    private void ShowMessage(string? message)
    {
        _messageTimer?.Dispose();
        _messageTimer = null;
        FooterText = message ?? Hints;
        if (message is not null)
        {
            _messageTimer = _clock.CreateTimer(_ => _ui.Post(() => FooterText = Hints), null, MessageDuration, Timeout.InfiniteTimeSpan);
        }
    }

    private void CancelDebounce()
    {
        _debounce?.Dispose();
        _debounce = null;
    }

    public void Dispose()
    {
        _disposed = true;
        CancelDebounce();
        _messageTimer?.Dispose();
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
