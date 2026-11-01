using System.Globalization;
using FolderNotes.Core.Domain;

namespace FolderNotes.App.Library;

/// <summary>Display strings for library rows and cards (specs/05 § Library behaviour). Pure functions.</summary>
public static class LibraryFormat
{
    /// <summary>Path budget in a list row.</summary>
    public const int RowPathChars = 36;

    /// <summary>Path budget on a card.</summary>
    public const int CardPathChars = 40;

    private const string Ellipsis = "…";

    /// <summary>
    /// Keeps the root (<c>D:\</c> or <c>\\server\share\</c>) and as many trailing segments as fit,
    /// with <c>…\</c> between them. If even the last segment doesn't fit, it's cut at the end.
    /// </summary>
    public static string TrimPathMiddle(string path, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxChars, 4);
        if (path.Length <= maxChars)
        {
            return path;
        }

        var rootLength = RootLength(path);
        var root = path[..rootLength];
        var segments = path[rootLength..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var prefix = root + Ellipsis + "\\";
        if (segments.Length == 0 || prefix.Length >= maxChars)
        {
            return CutEnd(path, maxChars);
        }

        // Add trailing segments while they fit.
        var tail = segments[^1];
        for (var i = segments.Length - 2; i >= 0; i--)
        {
            var candidate = segments[i] + "\\" + tail;
            if (prefix.Length + candidate.Length > maxChars)
            {
                break;
            }

            tail = candidate;
        }

        return prefix.Length + tail.Length <= maxChars ? prefix + tail : CutEnd(prefix + tail, maxChars);
    }

    /// <summary>
    /// The next date to show (05): the due date (review on / safe after, a tie counts as review),
    /// prefixed "Overdue: " when it's before today; otherwise a current keep-until; otherwise null.
    /// </summary>
    public static string? NextDate(Note note, DateOnly today, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(note);
        if (note.DueDate is { } due)
        {
            var kind = note.ReviewOn == due ? "Review" : "Safe after";
            var text = kind + " " + FormatDate(due, culture);
            return due < today ? "Overdue: " + text : text;
        }

        return note.KeepUntil is { } keep && keep >= today ? "Keep until " + FormatDate(keep, culture) : null;
    }

    /// <summary>True when the note's due date is before today.</summary>
    public static bool IsOverdue(Note note, DateOnly today) => note?.DueDate is { } due && due < today;

    /// <summary>"just now", "5 min ago", "3 h ago", "yesterday", "3 days ago", or the date (local time).</summary>
    public static string Relative(DateTimeOffset when, DateTimeOffset now, TimeZoneInfo zone, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var age = now - when;
        if (age < TimeSpan.FromMinutes(1))
        {
            return "just now"; // includes clock skew (future times)
        }

        if (age < TimeSpan.FromHours(1))
        {
            return $"{(int)age.TotalMinutes} min ago";
        }

        if (age < TimeSpan.FromHours(24))
        {
            return $"{(int)age.TotalHours} h ago";
        }

        var localWhen = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(when, zone).DateTime);
        var localNow = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var days = localNow.DayNumber - localWhen.DayNumber;
        return days switch
        {
            <= 1 => "yesterday",
            < 7 => $"{days} days ago",
            _ => FormatDate(localWhen, culture),
        };
    }

    public static string FormatDate(DateOnly date, CultureInfo culture) => date.ToString("d MMM yyyy", culture);

    private static int RootLength(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            // \\server\share\
            var server = path.IndexOf('\\', 2);
            if (server < 0)
            {
                return path.Length;
            }

            var share = path.IndexOf('\\', server + 1);
            return share < 0 ? path.Length : share + 1;
        }

        return path.Length >= 3 && path[1] == ':' && path[2] == '\\' ? 3 : 0;
    }

    private static string CutEnd(string text, int maxChars) => text[..(maxChars - 1)] + Ellipsis;
}
