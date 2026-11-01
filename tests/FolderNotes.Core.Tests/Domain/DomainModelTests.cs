using FolderNotes.Core.Domain;
using Microsoft.Extensions.Time.Testing;

namespace FolderNotes.Core.Tests.Domain;

public sealed class DomainModelTests
{
    [Theory]
    [InlineData(NoteStatus.None, "none")]
    [InlineData(NoteStatus.Keep, "keep")]
    [InlineData(NoteStatus.SafeToDelete, "safe-to-delete")]
    [InlineData(NoteStatus.Archive, "archive")]
    [InlineData(NoteStatus.InProgress, "in-progress")]
    [InlineData(NoteStatus.Review, "review")]
    public void REQ002_Status_RoundTripsThroughKey(NoteStatus status, string key)
    {
        status.ToKey().ShouldBe(key);
        NoteStatusExtensions.ParseKey(key).ShouldBe(status);
        NoteStatusExtensions.ParseKey(key.ToUpperInvariant()).ShouldBe(status);
    }

    [Fact]
    public void REQ002_Status_UnknownKey_FailsToParse()
    {
        NoteStatusExtensions.TryParseKey("deleted", out _).ShouldBeFalse();
        Should.Throw<FormatException>(() => NoteStatusExtensions.ParseKey("deleted"));
        Should.Throw<ArgumentOutOfRangeException>(() => ((NoteStatus)99).ToKey());
    }

    [Theory]
    [InlineData(VolumeKind.Fixed, "fixed")]
    [InlineData(VolumeKind.Removable, "removable")]
    [InlineData(VolumeKind.Network, "network")]
    public void REQ062_VolumeKind_RoundTrips(VolumeKind kind, string key)
    {
        kind.ToKey().ShouldBe(key);
        VolumeKindExtensions.ParseVolumeKind(key).ShouldBe(kind);
    }

    [Theory]
    [InlineData(@"C:\Clients\Acme Ltd", "Acme Ltd")]
    [InlineData(@"C:\Clients\Acme Ltd\", "Acme Ltd")]
    [InlineData(@"\\server\share\Docs", "Docs")]
    [InlineData(@"D:\", @"D:")]
    public void REQ003_FolderName_IsLastSegment(string path, string expected) =>
        new FolderRef(path.ToUpperInvariant(), path, null, null).FolderName.ShouldBe(expected);

    [Fact]
    public void REQ003_CreateFor_SuggestsFolderNameAsTitle()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 31, 9, 0, 0, TimeSpan.Zero));
        var note = Note.CreateFor(new FolderRef(@"C:\CLIENTS\ACME LTD", @"C:\Clients\Acme Ltd", null, null), clock);

        note.Title.ShouldBe("Acme Ltd");
        note.CreatedAt.ShouldBe(clock.GetUtcNow());
        note.UpdatedAt.ShouldBe(note.CreatedAt);
        note.Status.ShouldBe(NoteStatus.None);
        note.Labels.ShouldBeEmpty();
        NoteValidator.Validate(note).ShouldBeEmpty();
    }

    [Fact]
    public void REQ003_CreateFor_TruncatesVeryLongFolderNames()
    {
        var name = new string('n', 200);
        var note = Note.CreateFor(new FolderRef(@"C:\" + name.ToUpperInvariant(), @"C:\" + name, null, null));
        TextLength.Of(note.Title).ShouldBe(Note.MaxTitleLength);
        NoteValidator.Validate(note).ShouldBeEmpty();
    }

    [Fact]
    public void REQ020_DueDate_IsEarliestReminderDate()
    {
        var n = Note.CreateFor(new FolderRef("C:\\X", "C:\\X", null, null));
        n.DueDate.ShouldBeNull();
        (n with { ReviewOn = new DateOnly(2027, 5, 1) }).DueDate.ShouldBe(new DateOnly(2027, 5, 1));
        (n with { SafeAfter = new DateOnly(2027, 3, 1) }).DueDate.ShouldBe(new DateOnly(2027, 3, 1));
        (n with { ReviewOn = new DateOnly(2027, 5, 1), SafeAfter = new DateOnly(2027, 3, 1) }).DueDate.ShouldBe(new DateOnly(2027, 3, 1));
        (n with { KeepUntil = new DateOnly(2026, 1, 1) }).DueDate.ShouldBeNull();
    }

    [Fact]
    public void REQ060_FolderRef_HasFileId_NeedsBothParts()
    {
        new FolderRef("C:\\X", "C:\\X", "AB", "CD").HasFileId.ShouldBeTrue();
        new FolderRef("C:\\X", "C:\\X", null, "CD").HasFileId.ShouldBeFalse();
        new FolderRef("C:\\X", "C:\\X", "AB", null).HasFileId.ShouldBeFalse();
    }

    [Fact]
    public void REQ010_LeastUsedColour_TiesGoToPaletteOrder()
    {
        Palette.LeastUsed([]).ShouldBe("red");
        Palette.LeastUsed(["red", "orange"]).ShouldBe("amber");
        Palette.LeastUsed(Palette.Keys.Concat(["red", "orange"]).Concat(Palette.Keys.Skip(3))).ShouldBe("amber");
        Palette.LeastUsed(["not-a-colour"]).ShouldBe("red");
    }

    [Fact]
    public void REQ013_StarterLabels_AreValid()
    {
        Label.StarterLabels.Select(l => l.Name).ShouldBe(["client", "build-output", "archive", "personal", "do-not-delete"]);
        foreach (var (name, color) in Label.StarterLabels)
        {
            NoteValidator.ValidateLabel(Label.Create(name, color)).ShouldBeEmpty();
        }
    }

    [Fact]
    public void REQ010_LabelCreate_TrimsName() => Label.Create("  client ", "blue").Name.ShouldBe("client");

    [Fact]
    public void TextLength_TruncateKeepsSurrogatePairsWhole() => TextLength.Truncate("a📁b", 2).ShouldBe("a📁");
}
