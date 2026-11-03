namespace FolderNotes.App.Explorer;

/// <summary>Raised on the UI thread with the newest state (latest-wins, 04 L).</summary>
internal sealed class ActiveFolderChangedEventArgs(ActiveFolderState state) : EventArgs
{
    public ActiveFolderState State { get; } = state;
}

/// <summary>Diagnostics snapshot (02 <c>IExplorerTracker.GetStats</c>, 04 L). Plain data.</summary>
internal sealed record TrackerStats
{
    public long Evaluations { get; init; }

    public long Rebuilds { get; init; }

    public long Recoveries { get; init; }

    public long PublishedStates { get; init; }

    public double EvaluationP50Ms { get; init; }

    public double EvaluationP95Ms { get; init; }

    public double EvaluationMaxMs { get; init; }

    /// <summary>Workers abandoned so far in this process (the lifetime count of 04 B.6).</summary>
    public int AbandonedWorkers { get; init; }

    public int Replacements { get; init; }

    public int LiveRcws { get; init; }

    public int CrossThreadReleases { get; init; }

    public bool HooksActive { get; init; }

    public int CancelCalls { get; init; }

    public double MaxCancelCallMs { get; init; }

    public TrackerState State { get; init; }

    public DisabledReason DisabledReason { get; init; }

    public bool CanRetry { get; init; }

    public bool PermanentlyDisabled { get; init; }
}

/// <summary>Counters shared by every worker generation (they must survive a replacement). Thread-safe.</summary>
internal sealed class TrackerCounters
{
    private const int Window = 256;

    private readonly Lock _gate = new();
    private readonly double[] _ms = new double[Window];
    private long _evaluations;
    private long _rebuilds;
    private long _recoveries;
    private long _published;
    private int _next;
    private int _count;

    public long Evaluations => Interlocked.Read(ref _evaluations);

    public long Rebuilds => Interlocked.Read(ref _rebuilds);

    public long Recoveries => Interlocked.Read(ref _recoveries);

    public long PublishedStates => Interlocked.Read(ref _published);

    public void CountRebuild() => Interlocked.Increment(ref _rebuilds);

    public void CountRecovery() => Interlocked.Increment(ref _recoveries);

    public void CountPublished() => Interlocked.Increment(ref _published);

    public void RecordEvaluation(TimeSpan elapsed)
    {
        Interlocked.Increment(ref _evaluations);
        lock (_gate)
        {
            _ms[_next] = elapsed.TotalMilliseconds;
            _next = (_next + 1) % Window;
            _count = Math.Min(_count + 1, Window);
        }
    }

    /// <summary>p50, p95 and max over the last 256 evaluations.</summary>
    public (double P50, double P95, double Max) Percentiles()
    {
        double[] copy;
        lock (_gate)
        {
            if (_count == 0)
            {
                return default;
            }

            copy = _ms.AsSpan(0, _count).ToArray();
        }

        Array.Sort(copy);
        return (copy[(int)((copy.Length - 1) * 0.50)], copy[(int)((copy.Length - 1) * 0.95)], copy[^1]);
    }
}

/// <summary>The tracker as the rest of the app sees it (02 § ExplorerTracker, 04 L). Not started by anything until T3.4.</summary>
internal interface IExplorerTracker : IDisposable
{
    /// <summary>The last published state; <see cref="ActiveFolderState.IsInactive"/> when there is none.</summary>
    ActiveFolderState Current { get; }

    /// <summary>Raised on the UI thread, latest-wins.</summary>
    event EventHandler<ActiveFolderChangedEventArgs> ActiveFolderChanged;

    /// <summary>Idempotent. Refused (returns the reason) when disabled or another tracker is active.</summary>
    StartResult Start();

    /// <summary>Removes hooks and timers, releases COM, ends the threads; every wait is bounded. Publishes Inactive.</summary>
    void Stop();

    /// <summary>The user's explicit Retry after the tracker disabled itself (Settings, T3.4).</summary>
    RetryResult Retry();

    /// <summary>From the tray window's <c>TaskbarCreated</c> handler (04 F).</summary>
    void NotifyShellRestarted();

    TrackerStats GetStats();
}
