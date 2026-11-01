using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

static partial class Native
{
    public const uint COINIT_APARTMENTTHREADED = 2, COINIT_MULTITHREADED = 0;
    public const uint CLSCTX_LOCAL_SERVER = 4;
    public const uint WM_CLOSE = 0x10, WM_QUIT = 0x12;
    public const uint EVENT_OBJECT_NAMECHANGE = 0x800C;
    public const uint WINEVENT_OUTOFCONTEXT = 0, WINEVENT_SKIPOWNPROCESS = 2;

    public delegate void WinEventProc(nint hook, uint ev, nint hwnd, int idObject, int idChild, uint thread, uint time);

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG { public nint hwnd; public uint message; public nuint wParam; public nint lParam; public uint time; public int x, y; }

    [LibraryImport("ole32.dll")] public static partial int CoInitializeEx(nint reserved, uint flags);
    [LibraryImport("ole32.dll")] public static partial void CoUninitialize();
    [LibraryImport("ole32.dll")] public static partial int CoCreateInstance(in Guid clsid, nint outer, uint ctx, in Guid iid, out nint ppv);
    [LibraryImport("ole32.dll")] public static partial int CoEnableCallCancellation(nint reserved);
    [LibraryImport("ole32.dll")] public static partial int CoCancelCall(uint threadId, uint timeoutMs);
    [LibraryImport("ole32.dll")] public static partial int CoRegisterClassObject(in Guid clsid, nint unk, uint ctx, uint flags, out uint cookie);
    [LibraryImport("ole32.dll")] public static partial int CoRevokeClassObject(uint cookie);
    [LibraryImport("ole32.dll")] public static partial void CoTaskMemFree(nint p);
    [LibraryImport("shell32.dll")] public static partial int SHGetNameFromIDList(nint pidl, int sigdn, out nint name);
    [LibraryImport("shell32.dll")] public static partial void ILFree(nint pidl);
    [LibraryImport("shlwapi.dll", StringMarshalling = StringMarshalling.Utf16)] public static unsafe partial int PathCreateFromUrlW(string url, char* path, ref uint len, uint flags);

    [LibraryImport("user32.dll")] public static partial nint GetForegroundWindow();
    [LibraryImport("user32.dll")] public static partial nint GetShellWindow();
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] public static partial nint FindWindowExW(nint parent, nint after, string? cls, string? title);
    [LibraryImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] public static partial bool IsWindowVisible(nint h);
    [LibraryImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] public static partial bool IsWindow(nint h);
    [LibraryImport("user32.dll")] public static unsafe partial int GetClassNameW(nint h, char* buf, int max);
    [LibraryImport("user32.dll")] public static partial uint GetWindowThreadProcessId(nint h, out uint pid);
    [LibraryImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] public static partial bool PostMessageW(nint h, uint msg, nuint w, nint l);
    [LibraryImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] public static partial bool PostThreadMessageW(uint tid, uint msg, nuint w, nint l);
    [LibraryImport("user32.dll")] public static partial nint SetWinEventHook(uint min, uint max, nint mod, WinEventProc proc, uint pid, uint tid, uint flags);
    [LibraryImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] public static partial bool UnhookWinEvent(nint hook);
    [LibraryImport("user32.dll")] public static partial int GetMessageW(out MSG msg, nint h, uint min, uint max);
    [LibraryImport("user32.dll")] public static partial int PeekMessageW(out MSG msg, nint h, uint min, uint max, uint remove);
    [LibraryImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] public static partial bool TranslateMessage(in MSG msg);
    [LibraryImport("user32.dll")] public static partial nint DispatchMessageW(in MSG msg);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateWindowExW(uint ex, string cls, string title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint inst, nint param);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)][return: MarshalAs(UnmanagedType.Bool)] public static partial bool SetWindowTextW(nint h, string text);
    [LibraryImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] public static partial bool DestroyWindow(nint h);
    [LibraryImport("user32.dll")] public static partial void NotifyWinEvent(uint ev, nint hwnd, int idObject, int idChild);
    [LibraryImport("user32.dll")] public static partial nint GetAncestor(nint h, uint flags);
    [LibraryImport("user32.dll")] public static partial uint MsgWaitForMultipleObjects(uint n, nint handles, int waitAll, uint ms, uint mask);
    [LibraryImport("kernel32.dll")] public static partial uint GetCurrentThreadId();

    public static unsafe string ClassName(nint h) { char* b = stackalloc char[256]; int n = GetClassNameW(h, b, 256); return new string(b, 0, n); }
    public static uint PidOf(nint h) { GetWindowThreadProcessId(h, out var p); return p; }
}

/// <summary>Shared ComWrappers instance for the source-generated COM interfaces.</summary>
static class Com
{
    public static readonly StrategyBasedComWrappers Wrappers = new();

    /// <summary>Wraps a raw COM pointer (caller keeps and must release its own reference).</summary>
    public static T Wrap<T>(nint p) where T : class => (T)Wrappers.GetOrCreateObjectForComInstance(p, CreateObjectFlags.UniqueInstance);

    public static string Hr(int hr) => $"0x{hr:X8}";
}
