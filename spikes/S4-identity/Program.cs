// Spike S4: is FILE_ID_INFO a stable folder identity? Throwaway code, see specs/07-tasks.md.
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

var root = Path.Combine(Path.GetTempPath(), "fn-s4-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(root);
var results = new List<(string Case, string Expect, bool Pass, string Detail)>();
void Check(string c, string expect, bool pass, string detail = "") => results.Add((c, expect, pass, detail));

try
{
    var a = Path.Combine(root, "ClientA");
    Directory.CreateDirectory(a);
    File.WriteAllText(Path.Combine(a, "doc.txt"), "x");
    var id0 = Native.GetId(a);
    Console.WriteLine($"Volume serial {id0.Volume:X16}  FileId {id0.FileIdHex}");

    // 1 rename
    var renamed = Path.Combine(root, "ClientA-renamed");
    Directory.Move(a, renamed);
    Check("Rename", "same id", Native.GetId(renamed) == id0);

    // 2 move to a different parent (same volume)
    var sub = Directory.CreateDirectory(Path.Combine(root, "Archive", "2026")).FullName;
    var moved = Path.Combine(sub, "ClientA");
    Directory.Move(renamed, moved);
    Check("Move to other parent (same volume)", "same id", Native.GetId(moved) == id0);

    // 3 rename an ancestor
    var archive2 = Path.Combine(root, "Archive-old");
    Directory.Move(Path.Combine(root, "Archive"), archive2);
    var viaParent = Path.Combine(archive2, "2026", "ClientA");
    Check("Ancestor folder renamed", "same id", Native.GetId(viaParent) == id0);

    // 4 relink by id: OpenFileById + GetFinalPathNameByHandle
    var found = Native.PathFromId(root, id0);
    Check("OpenFileById relink", "finds new path", string.Equals(found, viaParent, StringComparison.OrdinalIgnoreCase), found ?? "null");

    // 5 copy gets a new identity (a copy must NOT inherit the note by id)
    var copy = Path.Combine(root, "ClientA-copy");
    CopyDir(viaParent, copy);
    Check("Copy", "different id", Native.GetId(copy) != id0);

    // 6 delete + recreate same path gets a new identity
    var tmp = Path.Combine(root, "Recreated");
    Directory.CreateDirectory(tmp);
    var idR = Native.GetId(tmp);
    Directory.Delete(tmp);
    Directory.CreateDirectory(tmp);
    Check("Delete + recreate same path", "different id", Native.GetId(tmp) != idR);

    // 7 deleted folder: OpenFileById fails (orphan)
    Directory.Delete(tmp);
    Check("Deleted folder relink", "not found", Native.PathFromId(root, idR) is null);

    // 8 Recycle Bin: id survives, path lands in $Recycle.Bin
    var rb = Path.Combine(root, "ToRecycle");
    Directory.CreateDirectory(rb);
    var idRb = Native.GetId(rb);
    Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(rb,
        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
    var rbPath = Native.PathFromId(root, idRb);
    Check("Sent to Recycle Bin", "found under $Recycle.Bin", rbPath?.Contains("$Recycle.Bin", StringComparison.OrdinalIgnoreCase) == true, rbPath ?? "null");
    if (rbPath is not null) { try { Directory.Delete(rbPath, true); } catch { } }

    // 9 cost
    var sw = System.Diagnostics.Stopwatch.StartNew();
    for (int i = 0; i < 1000; i++) Native.GetId(viaParent);
    var perCall = sw.Elapsed.TotalMilliseconds / 1000;
    Check("GetId cost", "< 1 ms", perCall < 1, $"{perCall:F3} ms/call");
    sw.Restart();
    for (int i = 0; i < 200; i++) Native.PathFromId(root, id0);
    var perRelink = sw.Elapsed.TotalMilliseconds / 200;
    Check("OpenFileById relink cost", "< 5 ms", perRelink < 5, $"{perRelink:F3} ms/call");

    // 10 subst drive alias: same id, different path string
    if (RunSubst($"Q: \"{archive2}\"") && Directory.Exists(@"Q:\2026\ClientA"))
    {
        Check("subst drive alias", "same id", Native.GetId(@"Q:\2026\ClientA") == id0, @"Q:\2026\ClientA");
        RunSubst("Q: /D");
    }
    else Check("subst drive alias", "same id", false, "subst unavailable, skipped");

    // 11 path spellings that should resolve to the same folder
    Check(@"\\?\ prefix", "same id", Native.GetId(@"\\?\" + viaParent) == id0);
    var shortName = Native.ShortPath(viaParent);
    Check("8.3 short path", "same id", shortName is not null && Native.GetId(shortName) == id0, shortName ?? "no short name");
    Check("Upper-cased path", "same id", Native.GetId(viaParent.ToUpperInvariant()) == id0);
}
finally
{
    try { Directory.Delete(root, true); } catch { }
}

Console.WriteLine();
Console.WriteLine("| Case | Expected | Result | Detail |");
Console.WriteLine("|---|---|---|---|");
foreach (var r in results) Console.WriteLine($"| {r.Case} | {r.Expect} | {(r.Pass ? "PASS" : "FAIL")} | {r.Detail} |");
return results.All(r => r.Pass || r.Detail.Contains("skipped")) ? 0 : 1;

static void CopyDir(string src, string dst)
{
    Directory.CreateDirectory(dst);
    foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)));
    foreach (var d in Directory.GetDirectories(src)) CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
}

static bool RunSubst(string args)
{
    try
    {
        var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("subst.exe", args) { UseShellExecute = false, CreateNoWindow = true })!;
        p.WaitForExit(5000);
        return p.ExitCode == 0;
    }
    catch { return false; }
}

readonly record struct FolderId(ulong Volume, ulong Lo, ulong Hi)
{
    public string FileIdHex => $"{Hi:X16}{Lo:X16}";
}

static unsafe class Native
{
    const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    const uint FILE_READ_ATTRIBUTES = 0x80;
    const uint SHARE_ALL = 7;
    const int FileIdInfo = 18;

    [StructLayout(LayoutKind.Sequential)] struct FILE_ID_INFO { public ulong VolumeSerialNumber; public ulong Lo; public ulong Hi; }
    // FILE_ID_DESCRIPTOR: DWORD dwSize; FILE_ID_TYPE Type; union { LARGE_INTEGER; GUID; FILE_ID_128 } (8-byte aligned)
    [StructLayout(LayoutKind.Sequential)] struct FILE_ID_DESCRIPTOR { public uint dwSize; public int Type; public ulong Lo; public ulong Hi; }

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disp, uint flags, IntPtr tmpl);
    [DllImport("kernel32", SetLastError = true)]
    static extern bool GetFileInformationByHandleEx(SafeFileHandle h, int cls, out FILE_ID_INFO info, uint size);
    [DllImport("kernel32", SetLastError = true)]
    static extern SafeFileHandle OpenFileById(SafeFileHandle hint, ref FILE_ID_DESCRIPTOR id, uint access, uint share, IntPtr sa, uint flags);
    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern uint GetFinalPathNameByHandleW(SafeFileHandle h, char* buf, uint len, uint flags);
    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern uint GetShortPathNameW(string longPath, char* buf, uint len);

    static SafeFileHandle Open(string path)
    {
        var h = CreateFileW(path, FILE_READ_ATTRIBUTES, SHARE_ALL, IntPtr.Zero, 3 /*OPEN_EXISTING*/, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (h.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), path);
        return h;
    }

    public static FolderId GetId(string path)
    {
        using var h = Open(path);
        if (!GetFileInformationByHandleEx(h, FileIdInfo, out var info, (uint)sizeof(FILE_ID_INFO)))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return new FolderId(info.VolumeSerialNumber, info.Lo, info.Hi);
    }

    public static string? PathFromId(string anyPathOnVolume, FolderId id)
    {
        using var hint = Open(Path.GetPathRoot(anyPathOnVolume)!);
        var d = new FILE_ID_DESCRIPTOR { dwSize = (uint)sizeof(FILE_ID_DESCRIPTOR), Type = 2 /*ExtendedFileIdType*/, Lo = id.Lo, Hi = id.Hi };
        using var h = OpenFileById(hint, ref d, FILE_READ_ATTRIBUTES, SHARE_ALL, IntPtr.Zero, FILE_FLAG_BACKUP_SEMANTICS);
        if (h.IsInvalid) return null;
        var buf = stackalloc char[1024];
        var n = GetFinalPathNameByHandleW(h, buf, 1024, 0);
        if (n == 0 || n >= 1024) return null;
        var s = new string(buf, 0, (int)n);
        return s.StartsWith(@"\\?\UNC\") ? @"\\" + s[8..] : s.StartsWith(@"\\?\") ? s[4..] : s;
    }

    public static string? ShortPath(string p)
    {
        var buf = stackalloc char[1024];
        var n = GetShortPathNameW(p, buf, 1024);
        if (n == 0 || n >= 1024) return null;
        var s = new string(buf, 0, (int)n);
        return string.Equals(s, p, StringComparison.OrdinalIgnoreCase) ? null : s;
    }
}
