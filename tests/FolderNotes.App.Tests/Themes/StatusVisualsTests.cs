using FolderNotes.App.Themes;
using FolderNotes.Core.Domain;

namespace FolderNotes.App.Tests.Themes;

/// <summary>05 § Visual language: every status has its icon, tint and text.</summary>
public sealed class StatusVisualsTests
{
    [Fact]
    public void REQ082_CoversEveryStatus_InEnumOrder() =>
        StatusVisuals.All.Select(v => v.Status).ShouldBe(Enum.GetValues<NoteStatus>());

    [Theory]
    [InlineData(NoteStatus.Keep, "blue", "Keep")]
    [InlineData(NoteStatus.SafeToDelete, "green", "Safe to delete")]
    [InlineData(NoteStatus.Archive, "indigo", "Archive")]
    [InlineData(NoteStatus.InProgress, "amber", "In progress")]
    [InlineData(NoteStatus.Review, "purple", "Review")]
    public void REQ082_StatusTintAndText_MatchTheSpec(NoteStatus status, string tint, string text)
    {
        var visual = StatusVisuals.For(status);
        visual.TintKey.ShouldBe(tint);
        Palette.IsValid(visual.TintKey).ShouldBeTrue();
        visual.Text.ShouldBe(text);
        visual.IconKey.ShouldBe("StatusIcon." + status.ToKey());
        visual.HasPill.ShouldBeTrue();
    }

    [Fact]
    public void None_HasNoPill()
    {
        var none = StatusVisuals.For(NoteStatus.None);
        none.HasPill.ShouldBeFalse();
        none.IconKey.ShouldBeNull();
        none.TintKey.ShouldBeNull();
    }

    [Fact]
    public void UnknownStatus_Throws() =>
        Should.Throw<ArgumentOutOfRangeException>(() => StatusVisuals.For((NoteStatus)42));
}
