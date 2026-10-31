using System.Runtime.InteropServices;
using System.Text;

static class Native
{
    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
    public const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;
    public const uint EVENT_OBJECT_SHOW = 0x8002;
    public const uint EVENT_OBJECT_HIDE = 0x8003;
    public const uint EVENT_OBJECT_NAMECHANGE = 0x800C;
    public const uint WINEVENT_OUTOFCONTEXT = 0;
    public const uint WINEVENT_SKIPOWNPROCESS = 2;
    public const uint WM_APP = 0x8000;
    public const uint WM_QUIT = 0x0012;
    public const uint WM_TIMER = 0x0113;

    public delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32")] public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr hmod, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32")] public static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string? cls, string? title);
    [DllImport("user32", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32")] public static extern uint GetCurrentThreadId();
    [DllImport("kernel32", EntryPoint = "GetCurrentThreadId")] public static extern uint GetCurrentThreadIdK();
    [DllImport("user32")] public static extern bool PostThreadMessageW(uint tid, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32")] public static extern int GetMessageW(out MSG m, IntPtr h, uint min, uint max);
    [DllImport("user32")] public static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32")] public static extern IntPtr DispatchMessageW(ref MSG m);
    [DllImport("user32")] public static extern UIntPtr SetTimer(IntPtr h, UIntPtr id, uint ms, IntPtr proc);
    [DllImport("user32")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int x, y; }

    public static string ClassName(IntPtr h)
    {
        var sb = new StringBuilder(256);
        GetClassNameW(h, sb, 256);
        return sb.ToString();
    }

    public static bool IsExplorerFrame(IntPtr h) => h != IntPtr.Zero && ClassName(h) == "CabinetWClass";

    /// Forces a window to the foreground from a background process (Alt-key trick).
    public static void ForceForeground(IntPtr h)
    {
        if (IsIconic(h)) ShowWindow(h, 9 /*SW_RESTORE*/);
        keybd_event(0x12, 0, 0, UIntPtr.Zero);       // Alt down
        keybd_event(0x12, 0, 2, UIntPtr.Zero);       // Alt up
        SetForegroundWindow(h);
    }

    public static void SendChord(params byte[] vks)
    {
        foreach (var vk in vks) keybd_event(vk, 0, 0, UIntPtr.Zero);
        foreach (var vk in vks.Reverse()) keybd_event(vk, 0, 2, UIntPtr.Zero);
    }
}

[ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IServiceProvider
{
    [PreserveSig] int QueryService(ref Guid service, ref Guid riid, out IntPtr ppv);
}

[ComImport, Guid("00000114-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IOleWindow
{
    [PreserveSig] int GetWindow(out IntPtr hwnd);
    [PreserveSig] int ContextSensitiveHelp(bool enter);
}
