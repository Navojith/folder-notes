using System.Runtime.InteropServices;

namespace FolderNotes.App.Explorer.Native;

/// <summary>The WinEvent callback shape (<c>WINEVENTPROC</c>).</summary>
internal delegate void WinEventCallback(nint hook, uint eventId, nint hwnd, int idObject, int idChild, uint threadId, uint time);

/// <summary>
/// The two WinEvent calls, behind an interface so the hook thread's lifecycle can be tested with a fake and so
/// the real calls are in one place. Only the hook thread calls these (04 B.7).
/// </summary>
internal interface IWinEventApi
{
    /// <summary>Install an out-of-context hook for events in [min, max]; <paramref name="processId"/> 0 = every process. Returns 0 on failure.</summary>
    nint SetHook(uint eventMin, uint eventMax, uint processId, WinEventCallback callback);

    /// <summary>Remove a hook. Must be called on the thread that installed it.</summary>
    bool Unhook(nint hook);
}

internal sealed partial class Win32WinEventApi(bool skipOwnProcess = true) : IWinEventApi
{
    private const uint WINEVENT_OUTOFCONTEXT = 0;
    private const uint WINEVENT_SKIPOWNPROCESS = 2;

    public nint SetHook(uint eventMin, uint eventMax, uint processId, WinEventCallback callback) =>
        SetWinEventHook(eventMin, eventMax, 0, callback, processId, 0,
            WINEVENT_OUTOFCONTEXT | (skipOwnProcess ? WINEVENT_SKIPOWNPROCESS : 0));

    public bool Unhook(nint hook) => UnhookWinEvent(hook);

    [LibraryImport("user32.dll")]
    private static partial nint SetWinEventHook(uint min, uint max, nint module, WinEventCallback proc, uint pid, uint tid, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnhookWinEvent(nint hook);
}

/// <summary>Win32 message-loop calls used by the hook thread and by the wake-up target.</summary>
internal static partial class MessageLoopNative
{
    public const uint WM_APP = 0x8000;
    public const uint PM_NOREMOVE = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public nint hwnd;
        public uint message;
        public nuint wParam;
        public nint lParam;
        public uint time;
        public int x;
        public int y;
    }

    [LibraryImport("user32.dll")]
    public static partial int GetMessageW(out MSG msg, nint hwnd, uint min, uint max);

    [LibraryImport("user32.dll")]
    public static partial int PeekMessageW(out MSG msg, nint hwnd, uint min, uint max, uint remove);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TranslateMessage(in MSG msg);

    [LibraryImport("user32.dll")]
    public static partial nint DispatchMessageW(in MSG msg);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostThreadMessageW(uint threadId, uint message, nuint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    public static partial void PostQuitMessage(int exitCode);

    [LibraryImport("kernel32.dll")]
    public static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll")]
    public static partial void NotifyWinEvent(uint eventId, nint hwnd, int idObject, int idChild);

    [LibraryImport("user32.dll")]
    public static partial nint GetDesktopWindow();
}

/// <summary>
/// Production <see cref="IWakeTarget"/>: posts one thread message to a worker's queue. The worker's loop turns it
/// into a call to <see cref="SignalRouter.Consume"/>. Identity (the object) is the worker's generation: a replacement
/// worker gets a new instance.
/// </summary>
internal sealed class ThreadMessageWakeTarget(uint threadId, uint message) : IWakeTarget
{
    public uint ThreadId { get; } = threadId;

    public uint Message { get; } = message;

    public bool TryPost() => MessageLoopNative.PostThreadMessageW(ThreadId, Message, 0, 0);
}
