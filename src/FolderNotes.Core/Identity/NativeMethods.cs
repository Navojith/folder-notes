using System.Runtime.InteropServices;
using FolderNotes.Core.Domain;
using Microsoft.Win32.SafeHandles;

namespace FolderNotes.Core.Identity;

/// <summary>Win32 calls used by <see cref="FolderIdentityService"/> (source-generated P/Invoke, no COM).</summary>
internal static unsafe partial class NativeMethods
{
    private const uint FileReadAttributes = 0x80;
    private const uint FileShareAll = 0x7;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileAttributeDirectory = 0x10;
    private const int FileBasicInfoClass = 0;
    private const int FileIdInfoClass = 18;
    private const int ExtendedFileIdType = 2;
    private const uint DriveRemovable = 2;
    private const uint DriveRemote = 4;
    private const uint DriveCdRom = 5;
    private const int PathBuffer = 32_768;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfo
    {
        public ulong VolumeSerialNumber;
        public ulong Lo;
        public ulong Hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileBasicInfo
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint FileAttributes;
    }

    // DWORD dwSize; FILE_ID_TYPE Type; union { LARGE_INTEGER; GUID; FILE_ID_128 } at offset 8.
    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdDescriptor
    {
        public uint Size;
        public int Type;
        public ulong Lo;
        public ulong Hi;
    }

    /// <summary>Opens a file or folder for reading attributes only, or returns null.</summary>
    public static SafeFileHandle? OpenForAttributes(string path)
    {
        var p = path.Length >= 2 && path[^1] == ':' ? path + @"\" : path; // "C:" means the current dir on C:
        var h = CreateFileW(p, FileReadAttributes, FileShareAll, 0, OpenExisting, FileFlagBackupSemantics, 0);
        if (h.IsInvalid)
        {
            h.Dispose();
            return null;
        }

        return h;
    }

    public static SafeFileHandle? OpenById(SafeFileHandle volumeHint, ulong lo, ulong hi)
    {
        var d = new FileIdDescriptor { Size = (uint)sizeof(FileIdDescriptor), Type = ExtendedFileIdType, Lo = lo, Hi = hi };
        var h = OpenFileById(volumeHint, &d, FileReadAttributes, FileShareAll, 0, FileFlagBackupSemantics);
        if (h.IsInvalid)
        {
            h.Dispose();
            return null;
        }

        return h;
    }

    public static bool TryGetFileId(SafeFileHandle h, out ulong volume, out ulong lo, out ulong hi)
    {
        FileIdInfo info;
        var ok = GetFileInformationByHandleEx(h, FileIdInfoClass, &info, (uint)sizeof(FileIdInfo));
        volume = ok ? info.VolumeSerialNumber : 0;
        lo = ok ? info.Lo : 0;
        hi = ok ? info.Hi : 0;
        return ok;
    }

    public static bool IsDirectory(SafeFileHandle h)
    {
        FileBasicInfo info;
        return GetFileInformationByHandleEx(h, FileBasicInfoClass, &info, (uint)sizeof(FileBasicInfo))
            && (info.FileAttributes & FileAttributeDirectory) != 0;
    }

    /// <summary><c>GetFinalPathNameByHandle</c> (DOS volume names), or null.</summary>
    public static string? GetFinalPath(SafeFileHandle h)
    {
        var buf = new char[PathBuffer];
        fixed (char* p = buf)
        {
            var n = GetFinalPathNameByHandleW(h, p, (uint)buf.Length, 0);
            return n == 0 || n >= buf.Length ? null : new string(p, 0, (int)n);
        }
    }

    public static VolumeKind GetVolumeKind(string displayPath)
    {
        if (displayPath.StartsWith(@"\\", StringComparison.Ordinal) && !displayPath.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return VolumeKind.Network;
        }

        var root = Path.GetPathRoot(displayPath);
        if (string.IsNullOrEmpty(root))
        {
            return VolumeKind.Fixed;
        }

        return GetDriveTypeW(root.EndsWith('\\') ? root : root + @"\") switch
        {
            DriveRemote => VolumeKind.Network,
            DriveRemovable or DriveCdRom => VolumeKind.Removable,
            _ => VolumeKind.Fixed,
        };
    }

    /// <summary>UNC target of a mapped network drive (<c>WNetGetConnection</c>), or null.</summary>
    public static string? GetMappedDriveTarget(char letter)
    {
        var buf = new char[1024];
        var len = buf.Length;
        fixed (char* p = buf)
        {
            return WNetGetConnectionW(letter + ":", p, ref len) == 0 ? new string(p) : null;
        }
    }

    /// <summary>True for NTFS and ReFS, the file systems whose 128-bit IDs survive rename/move (REQ-060).</summary>
    public static bool HasStableFileIds(SafeFileHandle h)
    {
        var fs = new char[64];
        fixed (char* p = fs)
        {
            if (!GetVolumeInformationByHandleW(h, null, 0, null, null, null, p, (uint)fs.Length))
            {
                return false;
            }

            var name = new string(p);
            return name is "NTFS" or "ReFS";
        }
    }

    public static IEnumerable<string> FixedDriveRoots() =>
        DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed).Select(d => d.RootDirectory.FullName);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleEx(SafeFileHandle h, int infoClass, void* info, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial SafeFileHandle OpenFileById(SafeFileHandle volumeHint, FileIdDescriptor* id, uint access, uint share, nint security, uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint GetFinalPathNameByHandleW(SafeFileHandle h, char* buffer, uint length, uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetVolumeInformationByHandleW(
        SafeFileHandle h, char* volumeName, uint volumeNameSize, uint* serial, uint* maxComponent, uint* flags, char* fsName, uint fsNameSize);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint GetDriveTypeW(string root);

    [LibraryImport("mpr.dll", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int WNetGetConnectionW(string localName, char* remoteName, ref int length);
}
