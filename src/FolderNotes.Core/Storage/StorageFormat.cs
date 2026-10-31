using System.Globalization;

namespace FolderNotes.Core.Storage;

/// <summary>Text formats for stored values (03 § Storage conventions).</summary>
internal static class StorageFormat
{
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
    private const string DateFormat = "yyyy-MM-dd";

    public static string Id(Guid id) => id.ToString("D");

    public static Guid ParseId(string s) => Guid.ParseExact(s, "D");

    /// <summary>Truncated to milliseconds, so a value read back equals the one written.</summary>
    public static DateTimeOffset Truncate(DateTimeOffset t)
    {
        var utc = t.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerMillisecond), TimeSpan.Zero);
    }

    public static string Timestamp(DateTimeOffset t) => Truncate(t).UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture);

    public static string? Timestamp(DateTimeOffset? t) => t is { } v ? Timestamp(v) : null;

    public static DateTimeOffset ParseTimestamp(string s) =>
        new(DateTime.SpecifyKind(DateTime.ParseExact(s, TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal), DateTimeKind.Utc));

    public static DateTimeOffset? ParseTimestampOrNull(string? s) => s is null ? null : ParseTimestamp(s);

    public static string? Date(DateOnly? d) => d?.ToString(DateFormat, CultureInfo.InvariantCulture);

    public static string Date(DateOnly d) => d.ToString(DateFormat, CultureInfo.InvariantCulture);

    public static DateOnly? ParseDate(string? s) => s is null ? null : DateOnly.ParseExact(s, DateFormat, CultureInfo.InvariantCulture);
}
