using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using FolderNotes.App.Themes;
using FolderNotes.Core.Domain;

namespace FolderNotes.App.Library;

/// <summary>
/// One note in the library (specs/05 § Library behaviour). Edits replace <see cref="Note"/> in place
/// (ADR-017); every display string follows.
/// </summary>
public sealed partial class LibraryRow : ObservableObject
{
    public const int MaxChips = 3;

    private readonly TimeProvider _clock;
    private readonly TimeZoneInfo _zone;
    private readonly CultureInfo _culture;

    internal LibraryRow(Note note, TimeProvider clock, TimeZoneInfo zone, CultureInfo culture)
    {
        _clock = clock;
        _zone = zone;
        _culture = culture;
        Note = note;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Id), nameof(Title), nameof(PathDisplay), nameof(RowPath), nameof(CardPath),
        nameof(Status), nameof(AccentColor), nameof(HasAccent), nameof(Chips), nameof(MoreLabels), nameof(IsOrphaned),
        nameof(FolderMissingText))]
    public partial Note Note { get; private set; }

    public Guid Id => Note.Id;

    public string Title => Note.Title;

    public string PathDisplay => Note.Folder.PathDisplay;

    public string RowPath => LibraryFormat.TrimPathMiddle(PathDisplay, LibraryFormat.RowPathChars);

    public string CardPath => LibraryFormat.TrimPathMiddle(PathDisplay, LibraryFormat.CardPathChars);

    public NoteStatus Status => Note.Status;

    /// <summary>Palette key, or "" when the note has no accent colour (the bar stays empty).</summary>
    public string AccentColor => Note.AccentColor ?? "";

    public bool HasAccent => Note.AccentColor is not null;

    /// <summary>The first <see cref="MaxChips"/> labels.</summary>
    public IReadOnlyList<Label> Chips => Note.Labels.Take(MaxChips).ToArray();

    /// <summary>"+n" for the labels beyond <see cref="MaxChips"/>, or "".</summary>
    public string MoreLabels => Note.Labels.Count > MaxChips ? "+" + (Note.Labels.Count - MaxChips).ToString(CultureInfo.InvariantCulture) : "";

    public bool IsOrphaned => Note.IsOrphaned;

    public string FolderMissingText => Note.IsOrphaned ? "Folder not found" : "";

    // Time-dependent strings: recomputed by RefreshTimes as well as on edits.
    [ObservableProperty]
    public partial string NextDate { get; private set; } = "";

    [ObservableProperty]
    public partial bool IsOverdue { get; private set; }

    [ObservableProperty]
    public partial string EditedText { get; private set; } = "";

    [ObservableProperty]
    public partial string AccessibleName { get; private set; } = "";

    internal void Update(Note note) => Note = note;

    /// <summary>Recomputes "Edited 3 h ago", the next date and the UIA name for the current time.</summary>
    internal void RefreshTimes()
    {
        var now = _clock.GetUtcNow();
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, _zone).DateTime);
        NextDate = LibraryFormat.NextDate(Note, today, _culture) ?? "";
        IsOverdue = LibraryFormat.IsOverdue(Note, today);
        EditedText = "Edited " + LibraryFormat.Relative(Note.UpdatedAt, now, _zone, _culture);
        AccessibleName = BuildAccessibleName();
    }

    // "<title>, <path>[, <status>][, labels <a, b>][, <next date>], edited <relative time>" (05).
    private string BuildAccessibleName()
    {
        var parts = new List<string> { Title, PathDisplay };
        if (Note.IsOrphaned)
        {
            parts.Add(FolderMissingText);
        }

        if (Status != NoteStatus.None)
        {
            parts.Add(StatusVisuals.For(Status).Text);
        }

        if (Note.Labels.Count > 0)
        {
            parts.Add("labels " + string.Join(", ", Note.Labels.Select(l => l.Name)));
        }

        if (NextDate.Length > 0)
        {
            parts.Add(NextDate);
        }

        parts.Add(EditedText.Replace("Edited ", "edited ", StringComparison.Ordinal));
        return string.Join(", ", parts);
    }

    partial void OnNoteChanged(Note value) => RefreshTimes();

    /// <summary>The list item's UIA name falls back to this (05 § Library behaviour, UIA).</summary>
    public override string ToString() => AccessibleName;
}
