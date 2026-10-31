using FolderNotes.Core.Domain;
using FolderNotes.Core.Identity;

namespace FolderNotes.Core.Tests.Identity;

/// <summary>Integration tests on real NTFS temp folders (specs/06-testing.md § Identity integration).</summary>
public sealed class FolderIdentityServiceTests : IDisposable
{
    private readonly string _root;
    private readonly FolderIdentityService _sut = new();

    public FolderIdentityServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fn-id-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Make(params string[] parts)
    {
        var p = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(p);
        return p;
    }

    [Fact]
    public void REQ060_Resolve_ExistingFolder_HasFileIdAndCanonicalPath()
    {
        var dir = Make("Client Acme");
        var r = _sut.Resolve(dir.ToLowerInvariant() + @"\");

        r.HasFileId.ShouldBeTrue();
        r.VolumeKind.ShouldBe(VolumeKind.Fixed);
        r.VolumeSerial!.Length.ShouldBe(16);
        r.FileId!.Length.ShouldBe(32);
        r.PathDisplay.ShouldEndWith(@"\Client Acme"); // canonical casing restored from the file system
        r.PathNorm.ShouldBe(r.PathDisplay.ToUpperInvariant());
    }

    [Fact]
    public void REQ060_Rename_KeepsFileId_AndLocateFindsNewPath()
    {
        var dir = Make("ClientA");
        var before = _sut.Resolve(dir);
        var renamed = Path.Combine(_root, "ClientA-renamed");
        Directory.Move(dir, renamed);

        var after = _sut.Resolve(renamed);
        (after.VolumeSerial, after.FileId).ShouldBe((before.VolumeSerial, before.FileId));

        var loc = _sut.Locate(before);
        loc.Status.ShouldBe(LocateStatus.Found);
        loc.Path.ShouldBe(after.PathDisplay);
    }

    [Fact]
    public void REQ060_MoveToOtherParent_KeepsFileId_AndLocateRelinks()
    {
        var dir = Make("ClientB");
        var before = _sut.Resolve(dir);
        var target = Path.Combine(Make("Archive", "2026"), "ClientB");
        Directory.Move(dir, target);

        var loc = _sut.Locate(before);
        loc.Status.ShouldBe(LocateStatus.Found);
        loc.Path.ShouldBe(_sut.Resolve(target).PathDisplay);
    }

    [Fact]
    public void REQ060_AncestorRenamed_KeepsFileId()
    {
        Make("Parent", "Child");
        var before = _sut.Resolve(Path.Combine(_root, "Parent", "Child"));
        Directory.Move(Path.Combine(_root, "Parent"), Path.Combine(_root, "Parent2"));

        var loc = _sut.Locate(before);
        loc.Status.ShouldBe(LocateStatus.Found);
        loc.Path!.ShouldEndWith(@"\Parent2\Child");
    }

    [Fact]
    public void REQ061_Delete_LocateReturnsNotFound()
    {
        var dir = Make("Gone");
        var before = _sut.Resolve(dir);
        Directory.Delete(dir);

        _sut.Locate(before).ShouldBe(LocateResult.NotFound);
    }

    [Fact]
    public void REQ061_SentToRecycleBin_IsDetected()
    {
        var dir = Make("ToRecycle");
        var before = _sut.Resolve(dir);
        Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
            dir, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);

        var loc = _sut.Locate(before);
        try
        {
            loc.Status.ShouldBe(LocateStatus.InRecycleBin);
            loc.Path!.ShouldContain("$Recycle.Bin", Case.Insensitive);
        }
        finally
        {
            if (loc.Path is not null && Directory.Exists(loc.Path))
            {
                Directory.Delete(loc.Path, recursive: true); // don't leave test junk in the user's bin
            }
        }
    }

    [Fact]
    public void REQ060_DeleteAndRecreate_GetsNewFileId()
    {
        var dir = Make("bin");
        var before = _sut.Resolve(dir);
        Directory.Delete(dir);
        Directory.CreateDirectory(dir);

        var after = _sut.Resolve(dir);
        after.PathNorm.ShouldBe(before.PathNorm);
        after.FileId.ShouldNotBe(before.FileId);
        _sut.Locate(before).Status.ShouldBe(LocateStatus.NotFound); // ADR-009: old ID doesn't resolve, so re-bind is allowed
    }

    [Fact]
    public void REQ060_Copy_GetsNewFileId()
    {
        var dir = Make("Original");
        var copy = Make("Copy");
        _sut.Resolve(copy).FileId.ShouldNotBe(_sut.Resolve(dir).FileId);
    }

    [Fact]
    public void REQ063_DifferentSpellings_ResolveToSameRef()
    {
        var dir = Make("Spelling Test");
        var a = _sut.Resolve(dir);

        _sut.Resolve(dir.ToUpperInvariant()).ShouldBe(a);
        _sut.Resolve(@"\\?\" + dir).ShouldBe(a);
        _sut.Resolve(dir.Replace('\\', '/') + "/").ShouldBe(a);
        _sut.Resolve("\"" + dir + "\"").ShouldBe(a);
    }

    [Fact]
    public void REQ063_ShortName_ResolvesToLongCanonicalPath()
    {
        var dir = Make("A Very Long Folder Name For 8dot3");
        var shortPath = ShortPath(dir);
        Assert.SkipWhen(shortPath is null, "8.3 names are disabled on this volume.");

        _sut.Resolve(shortPath!).ShouldBe(_sut.Resolve(dir));
    }

    [Fact]
    public void REQ062_MissingFolder_ResolvesByPathWithoutIds()
    {
        var missing = Path.Combine(_root, "does-not-exist");
        var r = _sut.Resolve(missing);

        r.HasFileId.ShouldBeFalse();
        r.PathDisplay.ShouldBe(missing);
        _sut.Locate(r).ShouldBe(LocateResult.NotFound);
    }

    [Fact]
    public void REQ062_PathOnlyRef_FoundWhileItExists()
    {
        var dir = Make("PathOnly");
        var r = new FolderRef(dir.ToUpperInvariant(), dir, null, null, VolumeKind.Network);

        _sut.Locate(r).ShouldBe(new LocateResult(LocateStatus.Found, dir));
        Directory.Delete(dir);
        _sut.Locate(r).ShouldBe(LocateResult.NotFound);
    }

    [Fact]
    public void REQ063_File_IsNotAFolder()
    {
        var file = Path.Combine(_root, "note.txt");
        File.WriteAllText(file, "x");

        _sut.TryResolve(file, out _).ShouldBeFalse();
        Should.Throw<ArgumentException>(() => _sut.Resolve(file));
    }

    [Theory]
    [InlineData("")]
    [InlineData(@"relative\path")]
    public void REQ063_InvalidPath_FailsToResolve(string path) => _sut.TryResolve(path, out _).ShouldBeFalse();

    [Fact]
    public void REQ060_CorruptStoredId_IsTreatedAsPathOnly()
    {
        var dir = Make("Corrupt");
        var r = _sut.Resolve(dir) with { FileId = "not-hex" };
        _sut.Locate(r).Status.ShouldBe(LocateStatus.Found);
    }

    [Fact]
    public void REQ060_HexFormat_RoundTrips()
    {
        var id = FolderIdentityService.FormatFileId(0x0102030405060708, 0xA0B0C0D0E0F00011);
        id.ShouldBe("A0B0C0D0E0F000110102030405060708");
        var r = new FolderRef("C:", "C:", FolderIdentityService.FormatSerial(0xBEEF), id);
        FolderIdentityService.TryParseIds(r, out var vol, out var lo, out var hi).ShouldBeTrue();
        (vol, lo, hi).ShouldBe((0xBEEFUL, 0x0102030405060708UL, 0xA0B0C0D0E0F00011UL));
    }

    [Fact]
    public void REQ060_Performance_ResolveIsCheap()
    {
        var dir = Make("Perf");
        _sut.Resolve(dir);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 200; i++)
        {
            _sut.Resolve(dir);
        }

        (sw.Elapsed.TotalMilliseconds / 200).ShouldBeLessThan(5);
    }

    private static string? ShortPath(string p)
    {
        var buf = new char[1024];
        var n = GetShortPathNameW(p, buf, buf.Length);
        if (n == 0 || n >= buf.Length)
        {
            return null;
        }

        var s = new string(buf, 0, n);
        return string.Equals(s, p, StringComparison.OrdinalIgnoreCase) ? null : s;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    private static extern int GetShortPathNameW(string longPath, char[] buffer, int length);
}
