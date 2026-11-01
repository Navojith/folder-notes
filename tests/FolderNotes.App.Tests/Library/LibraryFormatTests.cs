using System.Globalization;
using FolderNotes.App.Library;
using FolderNotes.Core.Domain;

namespace FolderNotes.App.Tests.Library;

/// <summary>specs/05 § Library behaviour: path trimming, next date, relative time.</summary>
public sealed class LibraryFormatTests
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly DateOnly Today = new(2026, 11, 1);

    [Theory]
    [InlineData(@"D:\Clients\Acme", 60, @"D:\Clients\Acme")]
    [InlineData(@"D:\Clients\Acme Ltd\Contracts\2026\Signed", 30, @"D:\…\Contracts\2026\Signed")]
    [InlineData(@"D:\Clients\Acme Ltd\Contracts\2026\Signed", 20, @"D:\…\2026\Signed")]
    [InlineData(@"D:\a\VeryLongFolderNameThatDoesNotFit", 20, @"D:\…\VeryLongFolder…")]
    [InlineData(@"\\nas\share\Photos\2019\Holiday\Raw", 30, @"\\nas\share\…\2019\Holiday\Raw")]
    [InlineData(@"\\averyveryverylongservername\share\x", 20, @"\\averyveryverylong…")]
    public void REQ054_TrimPathMiddle_KeepsRootAndTail(string path, int max, string expected)
    {
        var trimmed = LibraryFormat.TrimPathMiddle(path, max);
        trimmed.ShouldBe(expected);
        trimmed.Length.ShouldBeLessThanOrEqualTo(max);
    }

    [Fact]
    public void REQ054_NextDate_DueBeatsKeep_AndTiesCountAsReview()
    {
        var n = NewNote() with { ReviewOn = new(2026, 11, 3), SafeAfter = new(2026, 12, 1), KeepUntil = new(2027, 1, 1) };
        LibraryFormat.NextDate(n, Today, Inv).ShouldBe("Review 3 Nov 2026");
        LibraryFormat.NextDate(n with { ReviewOn = null }, Today, Inv).ShouldBe("Safe after 1 Dec 2026");
        LibraryFormat.NextDate(n with { SafeAfter = n.ReviewOn }, Today, Inv).ShouldBe("Review 3 Nov 2026");
    }

    [Fact]
    public void REQ054_NextDate_PastDue_IsOverdue()
    {
        var n = NewNote() with { SafeAfter = new(2026, 10, 31) };
        LibraryFormat.NextDate(n, Today, Inv).ShouldBe("Overdue: Safe after 31 Oct 2026");
        LibraryFormat.IsOverdue(n, Today).ShouldBeTrue();
        LibraryFormat.IsOverdue(n with { SafeAfter = Today }, Today).ShouldBeFalse();
    }

    [Fact]
    public void REQ054_NextDate_KeepUntil_OnlyWhileCurrent()
    {
        var n = NewNote() with { KeepUntil = Today };
        LibraryFormat.NextDate(n, Today, Inv).ShouldBe("Keep until 1 Nov 2026");
        LibraryFormat.NextDate(n with { KeepUntil = Today.AddDays(-1) }, Today, Inv).ShouldBeNull();
        LibraryFormat.NextDate(NewNote(), Today, Inv).ShouldBeNull();
    }

    [Theory]
    [InlineData(-5, "just now")]          // clock skew
    [InlineData(30, "just now")]
    [InlineData(5 * 60, "5 min ago")]
    [InlineData(3 * 3600 + 59, "3 h ago")]
    [InlineData(23 * 3600, "23 h ago")]   // still "h ago" although it was the previous day
    [InlineData(30 * 3600, "yesterday")]  // 2026-10-31 06:00 local
    [InlineData(3 * 86400, "3 days ago")]
    [InlineData(6 * 86400 + 3600, "6 days ago")]
    [InlineData(8 * 86400, "24 Oct 2026")]
    public void REQ054_Relative(int secondsAgo, string expected)
    {
        var now = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
        LibraryFormat.Relative(now.AddSeconds(-secondsAgo), now, TimeZoneInfo.Utc, Inv).ShouldBe(expected);
    }

    [Fact]
    public void REQ054_Relative_UsesLocalCalendarDays()
    {
        // 01:00 local on 1 Nov (UTC+5:30) vs 20:00 local on 30 Oct: two calendar days.
        var zone = TimeZoneInfo.CreateCustomTimeZone("ist", TimeSpan.FromHours(5.5), "IST", "IST");
        var now = new DateTimeOffset(2026, 11, 1, 1, 0, 0, TimeSpan.FromHours(5.5));
        var when = new DateTimeOffset(2026, 10, 30, 20, 0, 0, TimeSpan.FromHours(5.5));
        LibraryFormat.Relative(when, now, zone, Inv).ShouldBe("2 days ago");
    }

    private static Note NewNote() =>
        Note.CreateFor(new FolderRef(@"D:\X", @"D:\X", null, null), new Microsoft.Extensions.Time.Testing.FakeTimeProvider());
}
