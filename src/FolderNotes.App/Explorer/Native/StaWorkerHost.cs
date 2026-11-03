using System.Collections.Concurrent;
using FolderNotes.App.Explorer.Com;

namespace FolderNotes.App.Explorer.Native;

/// <summary>What the watchdog thread needs to know about a worker (04 B.6).</summary>
internal interface IWatchedWorker
{
    /// <summary>Win32 id of the worker thread (the target of <c>CoCancelCall</c>).</summary>
    uint ThreadId { get; }

    EvaluationWatchdog Watchdog { get; }
}

/// <summary>
/// The one COM thread (04 B.2): a named STA thread with its own message loop that owns every COM object. All the
/// work it does arrives as delegates that run <i>on it</i>, so no COM reference ever leaves the thread:
/// <list type="bullet">
/// <item><c>onStart</c> runs first (create the gateway there), <c>onStop</c> runs last (release everything there);</item>
/// <item><c>onWake</c> runs for every wake-up the <see cref="SignalRouter"/> posts (this host is the router's <see cref="IWakeTarget"/>);</item>
/// <item><see cref="Post"/> queues an action for the thread; actions run in order.</item>
/// </list>
/// Everything that can run COM calls goes through <see cref="Evaluate"/>, which brackets it for the watchdog. Nothing
/// thrown by a delegate escapes the loop. Stop is bounded: if the thread does not end it is <i>abandoned</i> (a
/// background thread, never joined, 04 B.6).
/// </summary>
internal sealed class StaWorkerHost : IWakeTarget, IWatchedWorker
{
    private const uint WmWake = MessageLoopNative.WM_APP + 2;
    private const uint WmRun = MessageLoopNative.WM_APP + 3;

    private readonly Action<StaWorkerHost> _onStart;
    private readonly Action<StaWorkerHost> _onWake;
    private readonly Action<StaWorkerHost> _onStop;
    private readonly Action<string> _log;
    private readonly Action? _afterBegin;
    private readonly ConcurrentQueue<Action> _posted = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Thread? _thread;
    private uint _threadId;
    private int _managedThreadId;
    private volatile bool _abandoned;
    private int _started;

    public StaWorkerHost(
        int generation,
        Action<StaWorkerHost> onStart,
        Action<StaWorkerHost> onWake,
        Action<StaWorkerHost> onStop,
        TimeProvider? clock = null,
        WatchdogOptions? watchdogOptions = null,
        Action<string>? log = null,
        Action? afterBegin = null)
    {
        Generation = generation;
        _onStart = onStart ?? throw new ArgumentNullException(nameof(onStart));
        _onWake = onWake ?? throw new ArgumentNullException(nameof(onWake));
        _onStop = onStop ?? throw new ArgumentNullException(nameof(onStop));
        Watchdog = new EvaluationWatchdog(clock ?? TimeProvider.System, watchdogOptions);
        _log = log ?? (_ => { });
        _afterBegin = afterBegin;
    }

    /// <summary>The publisher generation this worker was started for; its results carry it (04 B.6).</summary>
    public int Generation { get; }

    public EvaluationWatchdog Watchdog { get; }

    public uint ThreadId => Volatile.Read(ref _threadId);

    /// <summary>Managed thread id of the worker; every delegate runs on it (tests assert it).</summary>
    public int ManagedThreadId => Volatile.Read(ref _managedThreadId);

    /// <summary>True once the supervisor gave up on this thread. An abandoned worker produces nothing that is used.</summary>
    public bool IsAbandoned => _abandoned;

    public bool HasExited => _exited.Task.IsCompleted;

    /// <summary>Starts the thread and waits up to <paramref name="timeout"/> for <c>onStart</c> to finish. False = it did not (and the thread is abandoned).</summary>
    public bool Start(TimeSpan timeout)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("A worker host is started once; a replacement is a new host.");
        }

        _thread = new Thread(Run) { IsBackground = true, Name = "ExplorerTracker.Worker" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.UnsafeStart();
        if (_ready.Task.Wait(timeout) && !_exited.Task.IsCompleted)
        {
            return true;
        }

        MarkAbandoned();
        return false;
    }

    /// <summary>Queues an action for the worker thread; it runs in order, bracketed for the watchdog. False if the thread cannot be reached.</summary>
    public bool Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_abandoned || _exited.Task.IsCompleted)
        {
            return false;
        }

        _posted.Enqueue(action);
        uint tid = ThreadId;
        return tid != 0 && MessageLoopNative.PostThreadMessageW(tid, WmRun, 0, 0);
    }

    /// <summary>The router's wake-up: one thread message. A wedged worker holds at most this one unread message.</summary>
    public bool TryPost()
    {
        uint tid = ThreadId;
        return !_abandoned && tid != 0 && MessageLoopNative.PostThreadMessageW(tid, WmWake, 0, 0);
    }

    /// <summary>
    /// Runs <paramref name="work"/> on the calling (worker) thread between <c>Begin</c> and <c>End</c> of the watchdog,
    /// swallowing and logging any exception (04 B.5: "nothing throws out of the loop").
    /// </summary>
    public void Evaluate(Action work)
    {
        Watchdog.Begin();
        _afterBegin?.Invoke();
        try
        {
            work();
        }
        catch (Exception ex)
        {
            _log($"Worker evaluation failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Watchdog.End();
        }
    }

    /// <summary>
    /// Ask the thread to end, release its COM objects (<c>onStop</c> runs on it), and wait up to <paramref name="timeout"/>.
    /// True = ended in time. False = the thread did not end; it is abandoned (never joined) and must be counted by the caller.
    /// </summary>
    public bool Stop(TimeSpan timeout)
    {
        if (_thread is null || _exited.Task.IsCompleted)
        {
            return true;
        }

        if (!_abandoned)
        {
            _posted.Enqueue(StopAction);
            uint tid = ThreadId;
            if (tid != 0)
            {
                MessageLoopNative.PostThreadMessageW(tid, WmRun, 0, 0);
            }
        }

        if (_exited.Task.Wait(timeout))
        {
            return true;
        }

        MarkAbandoned();
        return false;
    }

    /// <summary>The supervisor declared this worker wedged: stop trusting it, and ask its loop to quit in case it ever returns.</summary>
    public void MarkAbandoned()
    {
        if (_abandoned)
        {
            return;
        }

        _abandoned = true;
        _posted.Enqueue(StopAction);
        uint tid = ThreadId;
        if (tid != 0)
        {
            MessageLoopNative.PostThreadMessageW(tid, WmRun, 0, 0);   // not dispatched inside a COM wait; read if the call ever returns
        }
    }

    private static readonly Action StopAction = static () => { };

    private void Run()
    {
        bool comReady = false;
        try
        {
            _managedThreadId = Environment.CurrentManagedThreadId;
            comReady = ComNative.CoInitializeEx(0, ComNative.CoinitApartmentThreaded) >= 0;
            if (ComNative.CoEnableCallCancellation(0) < 0)   // mandatory once per tracker thread, or CoCancelCall cannot cancel (S5/P2)
            {
                _log("CoEnableCallCancellation failed; a stuck call can only be abandoned, not cancelled.");
            }

            MessageLoopNative.PeekMessageW(out _, 0, 0, 0, MessageLoopNative.PM_NOREMOVE);   // creates the queue
            Volatile.Write(ref _threadId, MessageLoopNative.GetCurrentThreadId());

            Evaluate(() => _onStart(this));
            _ready.TrySetResult();

            bool quit = false;
            while (!quit && MessageLoopNative.GetMessageW(out var msg, 0, 0, 0) > 0)
            {
                switch (msg.message)
                {
                    case WmWake:
                        if (!_abandoned)
                        {
                            Evaluate(() => _onWake(this));
                        }

                        break;
                    case WmRun:
                        quit = DrainPosted();
                        break;
                    default:
                        MessageLoopNative.TranslateMessage(in msg);
                        MessageLoopNative.DispatchMessageW(in msg);
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            _log($"Worker thread failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try
            {
                _onStop(this);                              // release every COM object on the thread that made it
            }
            catch (Exception ex)
            {
                _log($"Worker cleanup failed: {ex.GetType().Name}: {ex.Message}");
            }

            if (comReady)
            {
                ComNative.CoUninitialize();
            }

            _ready.TrySetResult();
            _exited.TrySetResult();
        }
    }

    /// <summary>Runs queued actions in order; returns true when the stop marker was reached.</summary>
    private bool DrainPosted()
    {
        while (_posted.TryDequeue(out var action))
        {
            if (ReferenceEquals(action, StopAction))
            {
                return true;
            }

            if (!_abandoned)
            {
                Evaluate(action);
            }
        }

        return false;
    }
}
