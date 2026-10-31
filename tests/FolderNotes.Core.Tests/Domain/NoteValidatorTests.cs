using FolderNotes.Core.Domain;

namespace FolderNotes.Core.Tests.Domain;

public sealed class NoteValidatorTests
{
    private static readonly FolderRef s_folder = new(@"C:\CLIENTS\ACME", @"C:\Clients\Acme", "1A2B", "00FF", VolumeKind.Fixed);
    private static readonly DateTimeOffset s_t0 = new(2026, 10, 31, 10, 0, 0, TimeSpan.Zero);

    private static Note Valid() => new()
    {
        Id = Guid.NewGuid(),
        Folder = s_folder,
        Title = "Client: Acme Ltd",
        BodyMd = "Keep until the contract ends.",
        Labels = [Label.Create("client", "blue")],
        Status = NoteStatus.Keep,
        AccentColor = "teal",
        KeepUntil = new DateOnly(2027, 1, 1),
        CreatedAt = s_t0,
        UpdatedAt = s_t0,
    };

    private static IEnumerable<string> Codes(Note n) => NoteValidator.Validate(n).Select(e => e.Code);

    [Fact]
    public void REQ002_ValidNote_HasNoErrors() => NoteValidator.Validate(Valid()).ShouldBeEmpty();

    [Fact]
    public void REQ002_MinimalNote_TitleOnly_IsValid() =>
        NoteValidator.Validate(new Note { Id = Guid.NewGuid(), Folder = s_folder, Title = "x", CreatedAt = s_t0, UpdatedAt = s_t0 })
            .ShouldBeEmpty();

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void REQ002_Title_Required(string title) => Codes(Valid() with { Title = title }).ShouldContain("title.required");

    [Fact]
    public void REQ002_Title_120Chars_IsValid() => Codes(Valid() with { Title = new string('a', 120) }).ShouldBeEmpty();

    [Fact]
    public void REQ002_Title_121Chars_IsRejected() => Codes(Valid() with { Title = new string('a', 121) }).ShouldContain("title.too-long");

    [Fact]
    public void REQ002_Title_CountsEmojiAsOneCharacter()
    {
        var emoji = string.Concat(Enumerable.Repeat("📁", 120)); // 240 UTF-16 units, 120 characters
        Codes(Valid() with { Title = emoji }).ShouldBeEmpty();
        Codes(Valid() with { Title = emoji + "📁" }).ShouldContain("title.too-long");
    }

    [Theory]
    [InlineData("line1\nline2")]
    [InlineData("tab\there")]
    [InlineData("cr\r")]
    public void REQ002_Title_MustBeSingleLine(string title) => Codes(Valid() with { Title = title }).ShouldContain("title.single-line");

    [Fact]
    public void REQ002_Body_EmptyIsValid() => Codes(Valid() with { BodyMd = "" }).ShouldBeEmpty();

    [Fact]
    public void REQ002_Body_50kChars_IsValid() => Codes(Valid() with { BodyMd = new string('b', 50_000) }).ShouldBeEmpty();

    [Fact]
    public void REQ002_Body_Over50k_IsRejected() => Codes(Valid() with { BodyMd = new string('b', 50_001) }).ShouldContain("body.too-long");

    [Fact]
    public void REQ002_Body_MultilineMarkdown_IsValid() =>
        Codes(Valid() with { BodyMd = "# Heading\n\n- [ ] todo\r\n- **bold**" }).ShouldBeEmpty();

    [Theory]
    [InlineData(NoteStatus.None)]
    [InlineData(NoteStatus.Keep)]
    [InlineData(NoteStatus.SafeToDelete)]
    [InlineData(NoteStatus.Archive)]
    [InlineData(NoteStatus.InProgress)]
    [InlineData(NoteStatus.Review)]
    public void REQ002_Status_AllDefinedValuesAreValid(NoteStatus status) => Codes(Valid() with { Status = status }).ShouldBeEmpty();

    [Fact]
    public void REQ002_Status_UndefinedValue_IsRejected() => Codes(Valid() with { Status = (NoteStatus)42 }).ShouldContain("status.invalid");

    [Fact]
    public void REQ002_AccentColor_NullIsValid() => Codes(Valid() with { AccentColor = null }).ShouldBeEmpty();

    [Theory]
    [InlineData("magenta")]
    [InlineData("Blue")]
    [InlineData("#FF0000")]
    [InlineData("")]
    public void REQ002_AccentColor_NotInPalette_IsRejected(string color) => Codes(Valid() with { AccentColor = color }).ShouldContain("accent.invalid");

    [Fact]
    public void REQ002_Labels_EmptyListIsValid() => Codes(Valid() with { Labels = [] }).ShouldBeEmpty();

    [Fact]
    public void REQ002_Labels_ManyDistinct_AreValid() =>
        Codes(Valid() with { Labels = [.. Palette.Keys.Select((c, i) => Label.Create($"label-{i}", c))] }).ShouldBeEmpty();

    [Fact]
    public void REQ010_Labels_DuplicateNameIgnoringCase_IsRejected() =>
        Codes(Valid() with { Labels = [Label.Create("Client", "blue"), Label.Create("client", "red")] }).ShouldContain("labels.duplicate");

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void REQ010_LabelName_Required(string name) =>
        Codes(Valid() with { Labels = [new Label(Guid.NewGuid(), name, "blue")] }).ShouldContain("label.name-required");

    [Fact]
    public void REQ010_LabelName_40Chars_IsValid() =>
        Codes(Valid() with { Labels = [Label.Create(new string('l', 40), "blue")] }).ShouldBeEmpty();

    [Fact]
    public void REQ010_LabelName_Over40_IsRejected() =>
        Codes(Valid() with { Labels = [Label.Create(new string('l', 41), "blue")] }).ShouldContain("label.name-too-long");

    [Theory]
    [InlineData(" padded")]
    [InlineData("new\nline")]
    [InlineData("has\"quote")]
    public void REQ010_LabelName_Invalid_IsRejected(string name) =>
        Codes(Valid() with { Labels = [new Label(Guid.NewGuid(), name, "blue")] }).ShouldContain("label.name-invalid");

    [Fact]
    public void REQ010_LabelName_WithInnerSpaceAndUnicode_IsValid() =>
        Codes(Valid() with { Labels = [Label.Create("Mum's tax docs ✓", "pink")] }).ShouldBeEmpty();

    [Fact]
    public void REQ010_LabelColor_NotInPalette_IsRejected() =>
        Codes(Valid() with { Labels = [new Label(Guid.NewGuid(), "x", "brown")] }).ShouldContain("label.color-invalid");

    [Fact]
    public void REQ010_Palette_HasTwelveColors() => Palette.Keys.Distinct().Count().ShouldBe(12);

    [Fact]
    public void REQ002_Folder_Required() =>
        Codes(Valid() with { Folder = s_folder with { PathNorm = "" } }).ShouldContain("folder.required");

    [Fact]
    public void REQ002_UpdatedBeforeCreated_IsRejected() =>
        Codes(Valid() with { UpdatedAt = s_t0.AddSeconds(-1) }).ShouldContain("timestamps.order");

    [Fact]
    public void REQ020_AnyDateCombination_IsValid() =>
        Codes(Valid() with { KeepUntil = new DateOnly(2020, 1, 1), ReviewOn = new DateOnly(2030, 1, 1), SafeAfter = new DateOnly(2025, 6, 1) })
            .ShouldBeEmpty();

    [Fact]
    public void REQ002_MultipleViolations_AreAllReported() =>
        Codes(Valid() with { Title = "", BodyMd = new string('b', 50_001), AccentColor = "nope" })
            .ShouldBe(["title.required", "body.too-long", "accent.invalid"], ignoreOrder: true);

    [Fact]
    public void REQ002_EnsureValid_ThrowsWithErrors()
    {
        var ex = Should.Throw<NoteValidationException>(() => NoteValidator.EnsureValid(Valid() with { Title = "" }));
        ex.Errors.Single().Code.ShouldBe("title.required");
    }

    [Fact]
    public void REQ010_EnsureValidLabel_Throws() =>
        Should.Throw<NoteValidationException>(() => NoteValidator.EnsureValid(new Label(Guid.NewGuid(), "", "blue")));
}
