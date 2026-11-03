using System.Runtime.InteropServices;
using System.Text;

namespace FolderNotes.App.Tests.Explorer.Real;

/// <summary>
/// Win32 helpers for the real-Explorer verification (T3.2.5). Test code only: it drives Explorer windows that the lab
/// opened itself and never touches the user's own windows or the shell process.
/// </summary>
internal static partial class LabNative
{
    public const uint WM_CLOSE = 0x0010;
    public const byte VK_CONTROL = 0x11;
    public const byte VK_SHIFT = 0x10;
    public const byte VK_TAB = 0x09;
    public const byte VK_T = 0x54;
    public const byte VK_W = 0x57;
    public const byte VK_MENU = 0x12;
    public const byte VK_UP = 0x26;
    public const byte VK_N = 0x4E;

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool QueryProcessCycleTime(nint process, out ulong cycles);
    public const byte VK_BACK = 0x08;

    public const uint PROCESS_SUSPEND_RESUME = 0x0800;
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint PROCESS_TERMINATE = 0x0001;

    [LibraryImport("user32.dll")]
    public static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShowWindow(nint hwnd, int cmd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsIconic(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    public static partial nint GetShellWindow();

    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(nint hwnd, out uint pid);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessage(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetClassName(nint hwnd, [Out] char[] text, int max);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetWindowText(nint hwnd, [Out] char[] text, int max);

    [LibraryImport("user32.dll")]
    public static partial void keybd_event(byte vk, byte scan, uint flags, nuint extra);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetProcessHandleCount(nint process, out uint count);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetProcessTimes(nint process, out long creation, out long exit, out long kernel, out long user);

    [LibraryImport("kernel32.dll")]
    public static partial nint GetCurrentProcess();

    [LibraryImport("ntdll.dll")]
    public static partial int NtSuspendProcess(nint process);

    [LibraryImport("ntdll.dll")]
    public static partial int NtResumeProcess(nint process);

    public static string ClassName(nint hwnd)
    {
        var buf = new char[256];
        int n = GetClassName(hwnd, buf, buf.Length);
        return n > 0 ? new string(buf, 0, n) : string.Empty;
    }

    public static string Title(nint hwnd)
    {
        var buf = new char[512];
        int n = GetWindowText(hwnd, buf, buf.Length);
        return n > 0 ? new string(buf, 0, n) : string.Empty;
    }

    public static bool IsExplorerFrame(nint hwnd) => hwnd != 0 && ClassName(hwnd) == "CabinetWClass";

    public static uint PidOf(nint hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        return pid;
    }

    /// <summary>The Alt-key trick that lets a background process take the foreground. Returns whether it worked.</summary>
    public static bool ForceForeground(nint hwnd)
    {
        if (IsIconic(hwnd))
        {
            ShowWindow(hwnd, 9);              // SW_RESTORE
        }

        keybd_event(VK_MENU, 0, 0, 0);
        keybd_event(VK_MENU, 0, 2, 0);
        SetForegroundWindow(hwnd);
        for (int i = 0; i < 20 && GetForegroundWindow() != hwnd; i++)
        {
            Thread.Sleep(10);
        }

        return GetForegroundWindow() == hwnd;
    }

    public static void Chord(params byte[] vks)
    {
        foreach (var vk in vks)
        {
            keybd_event(vk, 0, 0, 0);
        }

        foreach (var vk in vks.Reverse())
        {
            keybd_event(vk, 0, 2, 0);
        }
    }

    /// <summary>User + kernel CPU time of this process, in milliseconds (100 ns units from the kernel).</summary>
    public static double ProcessCpuMs()
    {
        GetProcessTimes(GetCurrentProcess(), out _, out _, out var kernel, out var user);
        return (kernel + user) / 10_000.0;
    }

    /// <summary>
    /// CPU time of this process from the cycle counter (exact, unlike the 15.6 ms clock tick of <see cref="ProcessCpuMs"/>),
    /// converted with a calibration of cycles per millisecond measured on a short spin.
    /// </summary>
    public static double ProcessCycleCpuMs()
    {
        QueryProcessCycleTime(GetCurrentProcess(), out var cycles);
        return cycles / CyclesPerMs.Value;
    }

    private static readonly Lazy<double> CyclesPerMs = new(() =>
    {
        QueryProcessCycleTime(GetCurrentProcess(), out var c0);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        double x = 0;
        while (sw.ElapsedMilliseconds < 300)
        {
            x += Math.Sqrt(sw.ElapsedTicks);
        }

        QueryProcessCycleTime(GetCurrentProcess(), out var c1);
        GC.KeepAlive(x);
        return (c1 - c0) / sw.Elapsed.TotalMilliseconds;
    });

    public static uint HandleCount()
    {
        GetProcessHandleCount(GetCurrentProcess(), out var n);
        return n;
    }

    public static string Describe(nint hwnd) => $"0x{hwnd:X} ({ClassName(hwnd)}, pid {PidOf(hwnd)})";

    public static string Trim(StringBuilder sb) => sb.ToString().TrimEnd();
}
