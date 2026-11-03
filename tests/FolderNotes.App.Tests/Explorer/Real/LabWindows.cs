using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using FolderNotes.App.Explorer;
using FolderNotes.App.Explorer.Native;
using FolderNotes.App.Services;

namespace FolderNotes.App.Tests.Explorer.Real;

/// <summary>
/// A top-level window with its own thread and message loop, optionally owned by an Explorer frame. It stands in for the
/// docked panel (04 D.7: the panel is owned by the frame) and, unowned, for "some other app".
/// </summary>
internal sealed partial class LabWindow : IDisposable
{
    private const uint WS_POPUP = 0x80000000;
    private const uint WS_VISIBLE = 0x10000000;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WM_DESTROY = 0x0002;
    private const uint WM_QUIT = 0x0012;
    private static readonly WndProc Proc = DefProc;
    private static int _classCounter;

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private uint _threadId;

    [StructLayout(LayoutKind.Sequential)]
    private struct WndClassEx
    {
        public uint Size;
        public uint Style;
        public nint Proc;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public nint MenuName;
        public nint ClassName;
        public nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public nint Hwnd;
        public uint Message;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
    }

    private delegate nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam);

    public LabWindow(nint owner, string title)
    {
        Title = title;
        Owner = owner;
        _thread = new Thread(() => Run(owner, title)) { IsBackground = true, Name = "LabWindow" };
        _thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(5)) || Hwnd == 0)
        {
            throw new InvalidOperationException("The lab window could not be created.");
        }
    }

    public nint Hwnd { get; private set; }

    public nint Owner { get; }

    public string Title { get; }

    public void Dispose()
    {
        if (_threadId != 0)
        {
            PostThreadMessage(_threadId, WM_QUIT, 0, 0);
            _thread.Join(TimeSpan.FromSeconds(3));
        }
    }

    private void Run(nint owner, string title)
    {
        _threadId = GetCurrentThreadId();
        var name = $"FolderNotesLab{Interlocked.Increment(ref _classCounter)}";
        var namePtr = Marshal.StringToHGlobalUni(name);
        try
        {
            var wc = new WndClassEx
            {
                Size = (uint)Marshal.SizeOf<WndClassEx>(),
                Proc = Marshal.GetFunctionPointerForDelegate(Proc),
                ClassName = namePtr,
            };
            if (RegisterClassEx(ref wc) == 0)
            {
                return;
            }

            Hwnd = CreateWindowEx(WS_EX_TOOLWINDOW, name, title, WS_POPUP | WS_VISIBLE, 200, 200, 320, 200, owner, 0, 0, 0);
            _ready.Set();
            if (Hwnd == 0)
            {
                return;
            }

            while (GetMessage(out var m, 0, 0, 0) > 0)
            {
                TranslateMessage(ref m);
                DispatchMessage(ref m);
            }
        }
        finally
        {
            _ready.Set();
            Marshal.FreeHGlobal(namePtr);
        }
    }

    private static nint DefProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == WM_DESTROY)
        {
            PostQuitMessage(0);
        }

        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW")]
    private static partial ushort RegisterClassEx(ref WndClassEx wc);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWindowEx(uint exStyle, string cls, string title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static partial nint DefWindowProc(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "GetMessageW")]
    private static partial int GetMessage(out Msg msg, nint hwnd, uint min, uint max);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TranslateMessage(ref Msg msg);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static partial nint DispatchMessage(ref Msg msg);

    [LibraryImport("user32.dll")]
    private static partial void PostQuitMessage(int code);

    [LibraryImport("user32.dll", EntryPoint = "PostThreadMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostThreadMessage(uint threadId, uint msg, nuint wParam, nint lParam);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();
}

/// <summary>
/// A UI thread stand-in: a real thread draining a queue (<see cref="IUiDispatcher"/>), plus a heartbeat that measures how
/// late the queue runs work. The heartbeat is the "UI stays responsive while the tracker works" evidence.
/// </summary>
internal sealed class LabUi : IUiDispatcher, IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;
    private readonly Thread _beat;
    private readonly List<double> _lateMs = [];
    private volatile bool _stop;

    public LabUi(bool heartbeat = true)
    {
        _thread = new Thread(() =>
        {
            foreach (var work in _queue.GetConsumingEnumerable())
            {
                try
                {
                    work();
                }
                catch (Exception ex)
                {
                    LastError = ex;
                }
            }
        })
        { IsBackground = true, Name = "LabUi" };
        _thread.Start();
        _beat = new Thread(Beat) { IsBackground = true, Name = "LabUiBeat" };
        if (heartbeat)
        {
            _beat.Start();
        }
    }

    public Exception? LastError { get; private set; }

    public int ThreadId => _thread.ManagedThreadId;

    public bool Post(Action action)
    {
        try
        {
            return _queue.TryAdd(action);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Lateness of the 5 ms heartbeat: (p50, p99, max, count). The queue is the one the tracker posts to.</summary>
    public (double P50, double P99, double Max, int Count) Responsiveness()
    {
        double[] copy;
        lock (_lateMs)
        {
            copy = [.. _lateMs];
        }

        if (copy.Length == 0)
        {
            return default;
        }

        Array.Sort(copy);
        return (copy[(int)((copy.Length - 1) * 0.50)], copy[(int)((copy.Length - 1) * 0.99)], copy[^1], copy.Length);
    }

    public void ResetResponsiveness()
    {
        lock (_lateMs)
        {
            _lateMs.Clear();
        }
    }

    public void Dispose()
    {
        _stop = true;
        if (_beat.IsAlive)
        {
            _beat.Join(TimeSpan.FromSeconds(2));
        }

        _queue.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(2));
    }

    private void Beat()
    {
        while (!_stop)
        {
            long posted = Stopwatch.GetTimestamp();
            Post(() =>
            {
                double late = Stopwatch.GetElapsedTime(posted).TotalMilliseconds;
                lock (_lateMs)
                {
                    _lateMs.Add(late);
                }
            });
            Thread.Sleep(5);
        }
    }
}

/// <summary>Counts and optionally drops WinEvent callbacks in front of the real Win32 API (lost-event and scope tests).</summary>
internal sealed class LabWinEvents(IWinEventApi inner, Func<uint, bool>? drop = null, bool installNothing = false) : IWinEventApi
{
    private int _installs;
    private int _unhooks;
    private long _callbacks;
    private long _dropped;
    private int _live;
    private int _nextFake = 7000;
    private int _foreign;

    public List<string>? Trace { get; set; }

    /// <summary>Foreground changes to a window that is neither an Explorer window nor one of the lab's: someone else is using the desktop.</summary>
    public int ForeignForegrounds => Volatile.Read(ref _foreign);

    private static bool IsForeign(nint hwnd)
    {
        var cls = LabNative.ClassName(hwnd);
        return cls.Length > 0 && cls is not ("CabinetWClass" or "ShellTabWindowClass") && !cls.StartsWith("FolderNotesLab", StringComparison.Ordinal);
    }
    private readonly List<WinEventCallback> _keep = [];

    public int Installs => Volatile.Read(ref _installs);

    public int Unhooks => Volatile.Read(ref _unhooks);

    public long Callbacks => Interlocked.Read(ref _callbacks);

    public long Dropped => Interlocked.Read(ref _dropped);

    public int LiveHooks => Volatile.Read(ref _live);

    public List<uint> InstalledScopes { get; } = [];

    public nint SetHook(uint eventMin, uint eventMax, uint processId, WinEventCallback callback)
    {
        Interlocked.Increment(ref _installs);
        lock (InstalledScopes)
        {
            InstalledScopes.Add(processId);
        }

        if (installNothing)
        {
            Interlocked.Increment(ref _live);
            return Interlocked.Increment(ref _nextFake);            // "installed", but no event is ever delivered
        }

        WinEventCallback wrapper = (hook, ev, hwnd, obj, child, thread, time) =>
        {
            Interlocked.Increment(ref _callbacks);
            if (Trace is { } trace && ev == 0x0003)
            {
                lock (trace)
                {
                    trace.Add($"{DateTime.Now:HH:mm:ss.fff} FOREGROUND event: {LabNative.Describe(hwnd)}; the foreground window right now: {LabNative.Describe(LabNative.GetForegroundWindow())}");
                }
            }

            if (ev == 0x0003 && IsForeign(hwnd))
            {
                Interlocked.Increment(ref _foreign);
            }

            if (drop?.Invoke(ev) == true)
            {
                Interlocked.Increment(ref _dropped);
                return;
            }

            callback(hook, ev, hwnd, obj, child, thread, time);
        };
        lock (_keep)
        {
            _keep.Add(wrapper);                                     // the native hook holds only a function pointer: keep it alive
        }

        var h = inner.SetHook(eventMin, eventMax, processId, wrapper);
        if (h != 0)
        {
            Interlocked.Increment(ref _live);
        }

        return h;
    }

    public bool Unhook(nint hook)
    {
        Interlocked.Increment(ref _unhooks);
        Interlocked.Decrement(ref _live);
        return installNothing || inner.Unhook(hook);
    }
}
