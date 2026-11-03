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

    // ---- quarantine (04 § M.7, M.10) ----

    /// <summary>What the user is told: tracking, temporarily unavailable, quarantined, recovery attempt, paused, permanently off.</summary>
    public TrackerAvailability Availability { get; init; }

    public QuarantinePhase QuarantinePhase { get; init; }

    public int QuarantineEpisodes { get; init; }

    /// <summary>Unresolved entries: site, suspect kind, suspect pids and age.</summary>
    public IReadOnlyList<QuarantineEntrySummary> QuarantineEntries { get; init; } = [];

    /// <summary>Entries resolved because the blocked operation completed or its thread ended (E1).</summary>
    public int ReleasesByCompletion { get; init; }

    /// <summary>Entries resolved because the suspect server ended or was replaced (E2).</summary>
    public int ReleasesByServerEnd { get; init; }

    /// <summary>Records cleared by a passed trial.</summary>
    public int ReleasesByTrial { get; init; }

    public int TrialsStarted { get; init; }

    public int TrialsWedged { get; init; }

    public int TrialsInconclusive { get; init; }

    /// <summary>HRESULT of the most recent cancel request. Diagnostic only, never release evidence (04 § M.1).</summary>
    public int? LastCancelHr { get; init; }

    public CallSite LastWedgeSite { get; init; }

    /// <summary>Operations of the live worker found open at the start of an evaluation (must stay 0).</summary>
    public int JournalDefects { get; init; }

    /// <summary>Full enumerations of a validated root (trial evidence).</summary>
    public long FullEnumerations { get; init; }

    /// <summary>COM calls skipped because a frame was hung (04 § M.9).</summary>
    public long PreScreened { get; init; }
}

/// <summary>One unresolved quarantine entry, as plain data.</summary>
internal sealed record QuarantineEntrySummary(CallSite Site, JournalReadKind Snapshot, SuspectKind Suspect, IReadOnlyList<uint> SuspectPids, TimeSpan Age, int? CancelHr);

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
    private long _fullEnumerations;
    private long _preScreened;
    private int _next;
    private int _count;

    public long Evaluations => Interlocked.Read(ref _evaluations);

    public long Rebuilds => Interlocked.Read(ref _rebuilds);

    public long Recoveries => Interlocked.Read(ref _recoveries);

    public long PublishedStates => Interlocked.Read(ref _published);

    public void CountRebuild() => Interlocked.Increment(ref _rebuilds);

    public void CountRecovery() => Interlocked.Increment(ref _recoveries);

    public void CountPublished() => Interlocked.Increment(ref _published);

    /// <summary>Enumerations of a created and validated root that answered (04 § M.5: what a trial worker must achieve to pass).</summary>
    public long FullEnumerations => Interlocked.Read(ref _fullEnumerations);

    public void CountFullEnumeration() => Interlocked.Increment(ref _fullEnumerations);

    /// <summary>Calls not made because a frame was hung (04 § M.9 pre-screen).</summary>
    public long PreScreened => Interlocked.Read(ref _preScreened);

    public void CountPreScreened() => Interlocked.Increment(ref _preScreened);

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
