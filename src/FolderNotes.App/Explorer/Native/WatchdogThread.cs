using FolderNotes.App.Explorer.Com;

namespace FolderNotes.App.Explorer.Native;

/// <summary>
/// The watchdog thread of 04 B.6: one thread for the tracker's life that waits on an event, so it costs nothing while
/// no evaluation runs. For the one watched worker it applies <see cref="EvaluationWatchdog"/>'s rules: at the cancel
/// time it calls <c>CoCancelCall</c> on the worker thread; <c>AbandonAfterCancel</c> later it reports the worker as
/// wedged. It touches <b>neither hooks nor COM objects</b> (04 B.7): only the worker's thread id.
/// </summary>
internal sealed class WatchdogThread : IDisposable
{
    private readonly TrackerProcessGuard _guard;
    private readonly Action<IWatchedWorker> _onWedged;
    private readonly Action<uint> _cancel;
    private readonly Action<string> _log;
    private readonly AutoResetEvent _wake = new(false);
    private readonly Lock _gate = new();
    private readonly ManualResetEventSlim _exited = new(false);
    private IWatchedWorker? _watched;
    private Thread? _thread;
    private volatile bool _stopping;
    private int _slotHeld;
    private int _cancelCalls;
    private long _maxCancelTicks;
    private int _slowCancelLogged;

    /// <summary>A cancel slower than this is logged once and is the trigger for the contingency design (04 § H, P6).</summary>
    public static readonly TimeSpan SlowCancel = TimeSpan.FromMilliseconds(50);

    public WatchdogThread(TrackerProcessGuard guard, Action<IWatchedWorker> onWedged, Action<uint>? cancel = null, Action<string>? log = null)
    {
        _guard = guard ?? throw new ArgumentNullException(nameof(guard));
        _onWedged = onWedged ?? throw new ArgumentNullException(nameof(onWedged));
        _cancel = cancel ?? (static tid => _ = ComNative.CoCancelCall(tid, 0));
        _log = log ?? (_ => { });
    }

    /// <summary>How many times <c>CoCancelCall</c> was issued (diagnostics and tests).</summary>
    public int CancelCalls => Volatile.Read(ref _cancelCalls);

    /// <summary>Longest <c>CoCancelCall</c> so far, in milliseconds (reported in <c>TrackerStats</c>; 04 § H, P6).</summary>
    public double MaxCancelCallMs => TimeSpan.FromTicks(Interlocked.Read(ref _maxCancelTicks)).TotalMilliseconds;

    public bool IsRunning => _thread is not null && !_exited.IsSet;

    /// <summary>Reserves the budget slot and starts the thread. False if the budget refuses a second watchdog.</summary>
    public bool Start()
    {
        lock (_gate)
        {
            if (IsRunning || !_guard.TryReserveThread(TrackerThreadKind.Watchdog))
            {
                return false;
            }

            Volatile.Write(ref _slotHeld, 1);
            _stopping = false;
            _exited.Reset();
            _thread = new Thread(Run) { IsBackground = true, Name = "ExplorerTracker.Watchdog" };
            _thread.UnsafeStart();
            return true;
        }
    }

    /// <summary>Watch this worker (and only this one). Replaces the previous watched worker.</summary>
    public void Watch(IWatchedWorker worker)
    {
        lock (_gate)
        {
            _watched = worker;
        }

        _wake.Set();
    }

    /// <summary>Stop watching <paramref name="worker"/> if it is the watched one.</summary>
    public void Unwatch(IWatchedWorker worker)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_watched, worker))
            {
                _watched = null;
            }
        }

        _wake.Set();
    }

    /// <summary>Called by a worker after <c>Begin</c> so the thread re-reads its deadline.</summary>
    public void Poke() => _wake.Set();

    public bool Stop(TimeSpan timeout)
    {
        Thread? t;
        lock (_gate)
        {
            t = _thread;
            if (t is null)
            {
                return true;
            }

            _stopping = true;
            _watched = null;
        }

        _wake.Set();
        if (_exited.Wait(timeout))
        {
            lock (_gate)
            {
                _thread = null;
            }

            return true;
        }

        _log("Watchdog thread did not stop in time.");
        if (Interlocked.Exchange(ref _slotHeld, 0) == 1)
        {
            _guard.AbandonThread(TrackerThreadKind.Watchdog);
        }

        return false;
    }

    public void Dispose() => Stop(TimeSpan.FromSeconds(1));

    private void Run()
    {
        try
        {
            while (!_stopping)
            {
                IWatchedWorker? worker;
                lock (_gate)
                {
                    worker = _watched;
                }

                TimeSpan? wait = worker?.Watchdog.TimeUntilNextCheck();
                if (wait is null)
                {
                    _wake.WaitOne();                       // idle: no evaluation is running, so nothing to time
                    continue;
                }

                if (wait.Value > TimeSpan.Zero && _wake.WaitOne(wait.Value))
                {
                    continue;                              // re-read: a new evaluation began or the watch changed
                }

                if (worker is null)
                {
                    continue;
                }

                switch (worker.Watchdog.Check())
                {
                    case WatchdogAction.Cancel:
                        Interlocked.Increment(ref _cancelCalls);
                        SafeCancel(worker.ThreadId);
                        break;
                    case WatchdogAction.Abandon:
                        Unwatch(worker);
                        SafeReport(worker);
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            _log($"Watchdog thread failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (Interlocked.Exchange(ref _slotHeld, 0) == 1)
            {
                _guard.ReleaseThread(TrackerThreadKind.Watchdog);
            }

            _exited.Set();
        }
    }

    private void SafeCancel(uint threadId)
    {
        try
        {
            if (threadId != 0)
            {
                long start = System.Diagnostics.Stopwatch.GetTimestamp();
                try
                {
                    _cancel(threadId);
                }
                finally
                {
                    var took = System.Diagnostics.Stopwatch.GetElapsedTime(start);
                    long ticks = took.Ticks;
                    long seen;
                    while (ticks > (seen = Interlocked.Read(ref _maxCancelTicks)) && Interlocked.CompareExchange(ref _maxCancelTicks, ticks, seen) != seen)
                    {
                    }

                    if (took > SlowCancel && Interlocked.Exchange(ref _slowCancelLogged, 1) == 0)
                    {
                        _log($"CoCancelCall took {took.TotalMilliseconds:F0} ms (expected about 1 ms): see 04 H, P6.");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log($"CoCancelCall failed: {ex.GetType().Name}");
        }
    }

    private void SafeReport(IWatchedWorker worker)
    {
        try
        {
            _onWedged(worker);
        }
        catch (Exception ex)
        {
            _log($"Wedge handler failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
