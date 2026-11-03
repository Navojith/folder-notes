using System.Runtime.InteropServices;

namespace FolderNotes.App.Explorer.Native;

/// <summary>
/// The non-COM window queries the evaluation needs (04 L). Called on the worker thread only. Behind an interface so the
/// engine runs against a scripted Explorer in tests. None of these touches a COM object.
/// </summary>
internal interface IWin32Shell
{
    nint GetForegroundWindow();

    /// <summary>True for a top-level Explorer window (class <c>CabinetWClass</c>).</summary>
    bool IsExplorerFrame(nint hwnd);

    /// <summary>The owner window, 0 if none (the docked panel is owned by the Explorer frame, 04 D.7).</summary>
    nint OwnerOf(nint hwnd);

    bool IsMinimized(nint hwnd);

    /// <summary>The desktop shell window (<c>GetShellWindow</c>); 0 while Explorer is down.</summary>
    nint GetShellWindow();

    uint ProcessIdOf(nint hwnd);

    bool IsWindow(nint hwnd);

    /// <summary><c>ShellTabWindowClass</c> children of the frame in z-order, with their visibility (04 C).</summary>
    IReadOnlyList<ChildTab> TabChildren(nint frame);

    /// <summary>True if any Explorer frame exists (used to tell an empty private root from a real empty one, 04 F).</summary>
    bool AnyExplorerFrameExists();
}

internal sealed partial class Win32Shell : IWin32Shell
{
    private const string FrameClass = "CabinetWClass";
    private const string TabClass = "ShellTabWindowClass";
    private const uint GwOwner = 4;

    public nint GetForegroundWindow() => NativeGetForegroundWindow();

    public bool IsExplorerFrame(nint hwnd) => hwnd != 0 && ClassOf(hwnd) == FrameClass;

    public nint OwnerOf(nint hwnd) => hwnd == 0 ? 0 : NativeGetWindow(hwnd, GwOwner);

    public bool IsMinimized(nint hwnd) => hwnd != 0 && NativeIsIconic(hwnd);

    public nint GetShellWindow() => NativeGetShellWindow();

    public uint ProcessIdOf(nint hwnd)
    {
        if (hwnd == 0)
        {
            return 0;
        }

        _ = NativeGetWindowThreadProcessId(hwnd, out uint pid);
        return pid;
    }

    public bool IsWindow(nint hwnd) => hwnd != 0 && NativeIsWindow(hwnd);

    public IReadOnlyList<ChildTab> TabChildren(nint frame)
    {
        var result = new List<ChildTab>();
        if (frame == 0)
        {
            return result;
        }

        nint child = 0;
        for (int i = 0; i < 64; i++)                     // a frame never has anywhere near this many tabs; the bound keeps a bad answer finite
        {
            child = NativeFindWindowEx(frame, child, TabClass, null);
            if (child == 0)
            {
                break;
            }

            result.Add(new ChildTab(child, NativeIsWindowVisible(child)));
        }

        return result;
    }

    public bool AnyExplorerFrameExists() => NativeFindWindowEx(0, 0, FrameClass, null) != 0;

    private static unsafe string ClassOf(nint hwnd)
    {
        char* buffer = stackalloc char[64];
        int n = NativeGetClassName(hwnd, buffer, 64);
        return n > 0 ? new string(buffer, 0, n) : string.Empty;
    }

    [LibraryImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    private static partial nint NativeGetForegroundWindow();

    [LibraryImport("user32.dll", EntryPoint = "GetShellWindow")]
    private static partial nint NativeGetShellWindow();

    [LibraryImport("user32.dll", EntryPoint = "GetWindow")]
    private static partial nint NativeGetWindow(nint hwnd, uint command);

    [LibraryImport("user32.dll", EntryPoint = "IsIconic")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool NativeIsIconic(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "IsWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool NativeIsWindow(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "IsWindowVisible")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool NativeIsWindowVisible(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")]
    private static partial uint NativeGetWindowThreadProcessId(nint hwnd, out uint processId);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW")]
    private static unsafe partial int NativeGetClassName(nint hwnd, char* buffer, int max);

    [LibraryImport("user32.dll", EntryPoint = "FindWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint NativeFindWindowEx(nint parent, nint after, string? className, string? title);
}
