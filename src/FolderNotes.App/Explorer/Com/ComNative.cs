using System.Runtime.InteropServices;

namespace FolderNotes.App.Explorer.Com;

/// <summary>The few COM and shell calls the tracker's COM layer needs. Only the COM worker thread (and the watchdog's cancel) call these.</summary>
internal static partial class ComNative
{
    public const uint CoinitApartmentThreaded = 2;
    public const uint ClsctxLocalServer = 4;
    public const int SigdnFilesysPath = unchecked((int)0x80058000);
    public const int EFail = unchecked((int)0x80004005);
    public const int ENoInterface = unchecked((int)0x80004002);

    [LibraryImport("ole32.dll")]
    public static partial int CoInitializeEx(nint reserved, uint flags);

    [LibraryImport("ole32.dll")]
    public static partial void CoUninitialize();

    [LibraryImport("ole32.dll")]
    public static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint instance);

    /// <summary>Must run once on the calling thread before <see cref="CoCancelCall"/> can cancel its outgoing calls (S5/P2).</summary>
    [LibraryImport("ole32.dll")]
    public static partial int CoEnableCallCancellation(nint reserved);

    /// <summary>Cancels the call that thread <c>threadId</c> is blocked in (0 ms = at once). Called by the watchdog.</summary>
    [LibraryImport("ole32.dll")]
    public static partial int CoCancelCall(uint threadId, uint timeoutMs);

    [LibraryImport("ole32.dll")]
    public static partial void CoTaskMemFree(nint ptr);

    [LibraryImport("shell32.dll")]
    public static partial int SHGetNameFromIDList(nint pidl, int sigdn, out nint name);

    [LibraryImport("shell32.dll")]
    public static partial void ILFree(nint pidl);
}
