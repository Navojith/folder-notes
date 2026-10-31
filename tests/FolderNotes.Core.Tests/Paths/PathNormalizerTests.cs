using System.Text.Json;
using FolderNotes.Core.Paths;

namespace FolderNotes.Core.Tests.Paths;

public sealed class PathNormalizerTests
{
    private static readonly JsonSerializerOptions s_json = new() { PropertyNameCaseInsensitive = true };
    private static readonly Lazy<VectorFile> s_file = new(Load);

    public static TheoryData<string, string, string?, string> Vectors()
    {
        var data = new TheoryData<string, string, string?, string>();
        foreach (var v in s_file.Value.Vectors)
        {
            data.Add(v.Category, v.Input, v.Expected, v.Note);
        }

        return data;
    }

    [Fact]
    public void REQ063_VectorFile_HasAtLeast40Vectors_CoveringEveryCategory()
    {
        s_file.Value.Vectors.Count.ShouldBeGreaterThanOrEqualTo(40);
        var categories = s_file.Value.Vectors.Select(v => v.Category).ToHashSet();
        foreach (var c in new[] { "drive", "unc", "mapped", "prefix", "trailing", "casing", "long", "unicode", "invalid" })
        {
            categories.ShouldContain(c);
        }
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void REQ063_SharedVector(string category, string input, string? expected, string note)
    {
        var ok = PathNormalizer.TryNormalize(input, out var norm, Resolver);

        if (expected is null)
        {
            ok.ShouldBeFalse($"[{category}] {note}: expected invalid, got '{norm}'");
        }
        else
        {
            ok.ShouldBeTrue($"[{category}] {note}");
            norm.ShouldBe(expected, $"[{category}] {note}");
        }
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void REQ063_Normalize_IsIdempotent(string category, string input, string? expected, string note)
    {
        _ = category;
        _ = note;
        // Literal \\?\ inputs keep ".", ".." and trailing dots (rule 3), so they aren't idempotent by design.
        var literal = input.TrimStart().TrimStart('"').StartsWith(@"\\?\", StringComparison.Ordinal);
        if (expected is not null && !literal && !expected.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            PathNormalizer.Normalize(expected).ShouldBe(expected);
        }
    }

    [Fact]
    public void REQ063_Display_KeepsCasing_UppercasesDriveLetter()
    {
        PathNormalizer.NormalizeDisplay(@"c:\Clients\Acme Ltd\").ShouldBe(@"C:\Clients\Acme Ltd");
        PathNormalizer.NormalizeDisplay(@"\\?\UNC\Server\Share\Docs").ShouldBe(@"\\Server\Share\Docs");
        PathNormalizer.NormalizeDisplay("C:/a/./b/../Données").ShouldBe(@"C:\a\Données");
    }

    [Fact]
    public void REQ063_Normalize_InvalidThrows()
    {
        Should.Throw<ArgumentException>(() => PathNormalizer.Normalize(@"relative\path"));
        Should.Throw<ArgumentException>(() => PathNormalizer.NormalizeDisplay(""));
        PathNormalizer.TryNormalize(null, out _).ShouldBeFalse();
    }

    [Fact]
    public void REQ063_UnmappedLetter_StaysADrive() =>
        PathNormalizer.Normalize(@"X:\Data", Resolver).ShouldBe(@"X:\DATA");

    [Fact]
    public void REQ063_BadMappingTarget_IsInvalid() =>
        PathNormalizer.TryNormalize(@"Q:\x", out _, _ => @"not\unc").ShouldBeFalse();

    [Theory]
    [InlineData(@"C:\A\B", @"C:\A", true)]
    [InlineData(@"C:\A", @"C:\A", true)]
    [InlineData(@"C:\AB", @"C:\A", false)]
    [InlineData(@"C:\A", @"C:\A\B", false)]
    [InlineData(@"C:\A", "C:", true)]
    [InlineData(@"\\S\SH\X", @"\\S\SH", true)]
    [InlineData(@"D:\A", @"C:\A", false)]
    public void REQ052_IsSameOrUnder(string child, string folder, bool expected) =>
        PathNormalizer.IsSameOrUnder(child, folder).ShouldBe(expected);

    [Theory]
    [InlineData(@"C:\A\B", @"C:\A")]
    [InlineData(@"C:\A", "C:")]
    [InlineData("C:", null)]
    [InlineData(@"\\S\SH\X", @"\\S\SH")]
    [InlineData(@"\\S\SH", null)]
    public void REQ035_Parent(string norm, string? expected) => PathNormalizer.Parent(norm).ShouldBe(expected);

    private static string? Resolver(char letter) => s_file.Value.MappedDrives.GetValueOrDefault(letter.ToString());

    private static VectorFile Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "shared", "path-normalization-vectors.json");
        var file = JsonSerializer.Deserialize<VectorFile>(File.ReadAllText(path), s_json);
        return file ?? throw new InvalidOperationException("Empty vector file.");
    }

    private sealed record Vector(string Category, string Input, string? Expected, string Note);

    private sealed record VectorFile(string Schema, Dictionary<string, string> MappedDrives, List<Vector> Vectors);
}
