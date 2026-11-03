using System.Runtime.InteropServices;

namespace FolderNotes.App.Explorer.Native;

/// <summary>
/// The quarantine monitor's view of Explorer's processes (04 § M.4). Every call is a query that sends nothing to Explorer:
/// <c>OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)</c> with <c>GetProcessTimes</c>/<c>GetExitCodeProcess</c> (the handle is
/// closed at once; none is kept), <c>FindWindowEx</c>, <c>GetWindowThreadProcessId</c>, <c>IsHungAppWindow</c> and
/// <c>GetShellWindow</c>. No COM, no hooks, no window messages.
/// </summary>
internal sealed partial class Win32ServerProbe : IServerProbe
{
    private const string FrameClass = "CabinetWClass";
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint StillActive = 259;
    private const int ErrorInvalidParameter = 87;
    private const int MaxFrames = 512;

    /// <summary>Creation time recorded when the process exists but could not be opened (access denied): never treated as ended.</summary>
    private const long Unreadable = 0;

    public ServerIdentity? Identify(uint pid)
    {
        if (pid == 0)
        {
            return null;
        }

        nint h = NativeOpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (h == 0)
        {
            // No such process: gone. Any other failure (access denied) means it exists but cannot be read: keep it, conservatively.
            return Marshal.GetLastPInvokeError() == ErrorInvalidParameter ? null : new ServerIdentity(pid, Unreadable);
        }

        try
        {
            if (NativeGetExitCodeProcess(h, out uint code) && code != StillActive)
            {
                return null;                                 // exited; only someone's open handle keeps the object alive
            }

            return NativeGetProcessTimes(h, out long creation, out _, out _, out _) ? new ServerIdentity(pid, creation) : new ServerIdentity(pid, Unreadable);
        }
        finally
        {
            NativeCloseHandle(h);
        }
    }

    public bool IsRunning(ServerIdentity server)
    {
        var now = Identify(server.Pid);
        if (now is not { } current)
        {
            return false;
        }

        // An unreadable process is assumed to be the same one (never a false "ended", which would release a quarantine).
        return server.CreationTime == Unreadable || current.CreationTime == Unreadable || current.CreationTime == server.CreationTime;
    }

    public IReadOnlyList<uint> ExplorerFrameProcesses()
    {
        var pids = new List<uint>();
        foreach (nint frame in Frames())
        {
            uint pid = ProcessOf(frame);
            if (pid != 0 && !pids.Contains(pid))
            {
                pids.Add(pid);
            }
        }

        return pids;
    }

    public uint ShellProcessId() => ProcessOf(NativeGetShellWindow());

    public bool IsHung(ServerIdentity server)
    {
        if (!IsRunning(server))
        {
            return false;
        }

        nint shell = NativeGetShellWindow();
        if (shell != 0 && ProcessOf(shell) == server.Pid && NativeIsHungAppWindow(shell))
        {
            return true;
        }

        foreach (nint frame in Frames())
        {
            if (ProcessOf(frame) == server.Pid && NativeIsHungAppWindow(frame))
            {
                return true;
            }
        }

        return false;
    }

    public bool AnyExplorerFrameHung()
    {
        nint shell = NativeGetShellWindow();
        if (shell != 0 && NativeIsHungAppWindow(shell))
        {
            return true;
        }

        foreach (nint frame in Frames())
        {
            if (NativeIsHungAppWindow(frame))
            {
                return true;
            }
        }

        return false;
    }

    private static List<nint> Frames()
    {
        var frames = new List<nint>();
        nint frame = 0;
        for (int i = 0; i < MaxFrames; i++)                  // bounded: a bad answer stays finite
        {
            frame = NativeFindWindowEx(0, frame, FrameClass, null);
            if (frame == 0)
            {
                break;
            }

            frames.Add(frame);
        }

        return frames;
    }

    private static uint ProcessOf(nint hwnd)
    {
        if (hwnd == 0)
        {
            return 0;
        }

        _ = NativeGetWindowThreadProcessId(hwnd, out uint pid);
        return pid;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "OpenProcess", SetLastError = true)]
    private static partial nint NativeOpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [LibraryImport("kernel32.dll", EntryPoint = "CloseHandle")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool NativeCloseHandle(nint handle);

    [LibraryImport("kernel32.dll", EntryPoint = "GetExitCodeProcess")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool NativeGetExitCodeProcess(nint process, out uint exitCode);

    [LibraryImport("kernel32.dll", EntryPoint = "GetProcessTimes")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool NativeGetProcessTimes(nint process, out long creation, out long exit, out long kernel, out long user);

    [LibraryImport("user32.dll", EntryPoint = "IsHungAppWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool NativeIsHungAppWindow(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "GetShellWindow")]
    private static partial nint NativeGetShellWindow();

    [LibraryImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")]
    private static partial uint NativeGetWindowThreadProcessId(nint hwnd, out uint processId);

    [LibraryImport("user32.dll", EntryPoint = "FindWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint NativeFindWindowEx(nint parent, nint after, string? className, string? title);
}
