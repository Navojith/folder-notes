namespace FolderNotes.App.Explorer;

/// <summary>A process, identified so a reused pid is never mistaken for it (04 § M.3): pid plus creation time.</summary>
internal readonly record struct ServerIdentity(uint Pid, long CreationTime)
{
    /// <summary>Creation time used when the process had already ended when it was identified.</summary>
    public const long AlreadyEnded = -1;
}

internal enum SuspectKind
{
    /// <summary>The blocked operation named its target process.</summary>
    Known,

    /// <summary>The operation had no target pid (an enumeration item): every Explorer process and the shell, at most 8.</summary>
    Candidates,

    /// <summary>No active, consistent record: nothing is known about the server.</summary>
    Unknown,
}

/// <summary>Which server did not answer (04 § M.3).</summary>
internal sealed record Suspect(SuspectKind Kind, IReadOnlyList<ServerIdentity> Servers)
{
    public const int MaxCandidates = 8;

    public static Suspect Unknown { get; } = new(SuspectKind.Unknown, []);

    public static Suspect KnownServer(ServerIdentity server) => new(SuspectKind.Known, [server]);

    /// <summary>
    /// Conservative comparison for the per-server trial budget (04 § M.5): an unknown suspect matches everything; otherwise
    /// two suspects are the same server when they share an identity.
    /// </summary>
    public bool SameServerAs(Suspect other) =>
        Kind == SuspectKind.Unknown || other.Kind == SuspectKind.Unknown || Servers.Any(other.Servers.Contains);

    /// <summary>Builds the suspect from the journal snapshot taken at abandonment (watchdog thread; Win32 queries only).</summary>
    public static Suspect From(JournalSnapshot snapshot, IServerProbe probe)
    {
        if (!snapshot.IsActive)
        {
            return Unknown;                                      // idle, torn or stale: nothing reliable (04 § M.2)
        }

        if (snapshot.Record.TargetPid != 0)
        {
            return KnownServer(Identify(probe, snapshot.Record.TargetPid));
        }

        var pids = probe.ExplorerFrameProcesses().Append(probe.ShellProcessId()).Where(p => p != 0).Distinct().ToList();
        if (pids.Count == 0 || pids.Count > MaxCandidates)
        {
            return Unknown;
        }

        return new Suspect(SuspectKind.Candidates, pids.Select(p => Identify(probe, p)).ToList());
    }

    private static ServerIdentity Identify(IServerProbe probe, uint pid) =>
        probe.Identify(pid) ?? new ServerIdentity(pid, ServerIdentity.AlreadyEnded);
}

/// <summary>
/// The Win32 view of Explorer's processes that quarantine needs (04 § M.4). Implemented with calls that send nothing to
/// Explorer (<c>OpenProcess</c> + times, <c>FindWindowEx</c>, <c>GetWindowThreadProcessId</c>, <c>IsHungAppWindow</c>,
/// <c>GetShellWindow</c>); used on the watchdog thread and, for Retry, on the caller's thread. No COM.
/// </summary>
internal interface IServerProbe
{
    /// <summary>The identity of the process with this pid now, or null if there is none.</summary>
    ServerIdentity? Identify(uint pid);

    /// <summary>True while that exact process (pid and creation time) is still running.</summary>
    bool IsRunning(ServerIdentity server);

    /// <summary>Processes that own a top-level Explorer frame (<c>CabinetWClass</c>).</summary>
    IReadOnlyList<uint> ExplorerFrameProcesses();

    /// <summary>Process of <c>GetShellWindow()</c>, 0 while the shell is down.</summary>
    uint ShellProcessId();

    /// <summary>Hang evidence for one server: <c>IsHungAppWindow</c> on any of its Explorer frames, or on the shell window if it is the shell.</summary>
    bool IsHung(ServerIdentity server);

    /// <summary>Hang evidence for an unknown suspect: any Explorer frame (or the shell window) is hung.</summary>
    bool AnyExplorerFrameHung();
}

/// <summary>Quarantine timing (04 § M, user decision D6). Tests scale it down.</summary>
internal sealed record QuarantineOptions
{
    public static QuarantineOptions Default { get; } = new();

    /// <summary>E3: how long, without a break, no hang evidence must be seen.</summary>
    public TimeSpan HealthHold { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>E3: the least time since the most recent abandonment before an automatic trial.</summary>
    public TimeSpan MinimumQuarantine { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How often the monitor looks while anything is unresolved.</summary>
    public TimeSpan CheckInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>A trial worker passes or fails at this deadline after it started.</summary>
    public TimeSpan TrialDeadline { get; init; } = TimeSpan.FromSeconds(10);
}

internal enum QuarantineResolution
{
    None,

    /// <summary>E1: the blocked operation completed.</summary>
    OperationCompleted,

    /// <summary>E1: the abandoned thread ended.</summary>
    WorkerExited,

    /// <summary>E2: the suspect process ended or was replaced.</summary>
    ServerEnded,
}

/// <summary>One abandonment (04 § M.3). Plain data plus the zombie's read-only view.</summary>
internal sealed class QuarantineEntry(IAbandonedWorkerView zombie, JournalSnapshot snapshot, Suspect suspect, int? cancelHr, long abandonedAt)
{
    public IAbandonedWorkerView Zombie { get; } = zombie;

    /// <summary>The journal as read at abandonment.</summary>
    public JournalSnapshot Snapshot { get; } = snapshot;

    /// <summary>May narrow from candidates to one known server (04 § M.4).</summary>
    public Suspect Suspect { get; internal set; } = suspect;

    /// <summary>The cancel request's HRESULT. Diagnostic only: never evidence (04 § M.1).</summary>
    public int? CancelHr { get; } = cancelHr;

    public long AbandonedAt { get; } = abandonedAt;

    public QuarantineResolution Resolution { get; internal set; }

    /// <summary>E1 then E2 (04 § M.4). Never looks at <see cref="CancelHr"/>.</summary>
    internal QuarantineResolution Evaluate(IServerProbe probe)
    {
        if (Zombie.HasExited)
        {
            return QuarantineResolution.WorkerExited;
        }

        if (Snapshot.IsActive && Zombie.HasCompleted(Snapshot.Record.OperationId))
        {
            return QuarantineResolution.OperationCompleted;
        }

        if (Suspect.Kind != SuspectKind.Unknown && Suspect.Servers.All(s => s.CreationTime == ServerIdentity.AlreadyEnded || !probe.IsRunning(s)))
        {
            return QuarantineResolution.ServerEnded;
        }

        return QuarantineResolution.None;
    }

    internal bool ShowsHangEvidence(IServerProbe probe) =>
        Suspect.Kind == SuspectKind.Unknown
            ? probe.AnyExplorerFrameHung()
            : Suspect.Servers.Any(s => s.CreationTime != ServerIdentity.AlreadyEnded && probe.IsHung(s));
}

/// <summary>The abandoned worker as quarantine sees it (E1). Implemented by the worker host; a fake in tests.</summary>
internal interface IAbandonedWorkerView
{
    bool HasExited { get; }

    bool HasCompleted(long operationId);
}

/// <summary>What one monitor look found (04 § M.4).</summary>
internal readonly record struct QuarantineAssessment(bool AllResolved, bool HangEvidence, bool HoldComplete, bool AutomaticTrialAllowed);

/// <summary>
/// The process-wide quarantine record (04 § M): unresolved entries, the health hold, and which servers already had their
/// automatic trial. Lives in <see cref="TrackerProcessGuard"/>, so Stop/Start and new tracker instances meet it.
/// Thread-safe (own lock); it never calls the state machine, so the lock order is always machine → record.
/// </summary>
internal sealed class QuarantineRecord
{
    private readonly Lock _gate = new();
    private readonly TimeProvider _clock;
    private readonly List<QuarantineEntry> _entries = [];
    private readonly List<Suspect> _tried = [];
    private long? _holdSince;
    private long _lastAbandonedAt;
    private int _episodes;
    private int _releasedByCompletion;
    private int _releasedByServerEnd;
    private int _releasedByTrial;

    public QuarantineRecord(TimeProvider? clock = null, QuarantineOptions? options = null)
    {
        _clock = clock ?? TimeProvider.System;
        Options = options ?? QuarantineOptions.Default;
    }

    public QuarantineOptions Options { get; }

    public TimeProvider Clock => _clock;

    public bool HasUnresolved
    {
        get { lock (_gate) { return _entries.Count > 0; } }
    }

    public IReadOnlyList<QuarantineEntry> Entries
    {
        get { lock (_gate) { return [.. _entries]; } }
    }

    /// <summary>Times the record went from empty to non-empty.</summary>
    public int Episodes
    {
        get { lock (_gate) { return _episodes; } }
    }

    /// <summary>Entries resolved by E1, entries resolved by E2, and records cleared by a passed trial.</summary>
    public (int ByCompletion, int ByServerEnd, int ByTrial) Releases
    {
        get { lock (_gate) { return (_releasedByCompletion, _releasedByServerEnd, _releasedByTrial); } }
    }

    /// <summary>Records an abandonment. The hold starts again from zero.</summary>
    public void Add(QuarantineEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            if (_entries.Count == 0)
            {
                _episodes++;
            }

            _entries.Add(entry);
            _lastAbandonedAt = entry.AbandonedAt;
            _holdSince = null;
        }
    }

    /// <summary>
    /// One monitor look: resolves entries by E1/E2, narrows candidate sets, updates the health hold and says whether an
    /// automatic trial is allowed. When the last entry resolves, the episode ends (hold and trial budget reset).
    /// </summary>
    public QuarantineAssessment Assess(IServerProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        lock (_gate)
        {
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                var e = _entries[i];
                var resolution = e.Evaluate(probe);
                if (resolution == QuarantineResolution.None)
                {
                    continue;
                }

                e.Resolution = resolution;
                if (resolution == QuarantineResolution.ServerEnded)
                {
                    _releasedByServerEnd++;
                }
                else
                {
                    _releasedByCompletion++;
                }

                _entries.RemoveAt(i);
            }

            if (_entries.Count == 0)
            {
                EndEpisode();
                return new QuarantineAssessment(true, false, false, false);
            }

            bool hang = false;
            foreach (var e in _entries)
            {
                Narrow(e, probe);
                hang |= e.ShowsHangEvidence(probe);
            }

            long now = _clock.GetTimestamp();
            if (hang)
            {
                _holdSince = null;                               // the hold starts again from zero (04 § M.4)
            }
            else
            {
                _holdSince ??= now;
            }

            bool holdComplete = _holdSince is { } since
                && _clock.GetElapsedTime(since, now) >= Options.HealthHold
                && _clock.GetElapsedTime(_lastAbandonedAt, now) >= Options.MinimumQuarantine;
            bool untried = _entries.Any(e => !_tried.Any(t => t.SameServerAs(e.Suspect)));
            return new QuarantineAssessment(false, hang, holdComplete, untried);
        }
    }

    /// <summary>Hang evidence right now, for a Retry (04 § M.6 Q21/Q22). Does not move the hold.</summary>
    public bool HangEvidenceNow(IServerProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        lock (_gate)
        {
            return _entries.Any(e => e.ShowsHangEvidence(probe));
        }
    }

    /// <summary>A trial is starting: every current suspect has now had its trial (04 § M.5). The hold starts again.</summary>
    public void MarkTried()
    {
        lock (_gate)
        {
            foreach (var e in _entries)
            {
                _tried.Add(e.Suspect);
            }

            _holdSince = null;
        }
    }

    /// <summary>A trial ended without passing: another complete hold is needed (04 § M.5).</summary>
    public void RestartHold()
    {
        lock (_gate)
        {
            _holdSince = null;
        }
    }

    /// <summary>A trial passed: the record is cleared (04 § M.5, Q8).</summary>
    public void ClearByTrial()
    {
        lock (_gate)
        {
            if (_entries.Count > 0)
            {
                _releasedByTrial++;
            }

            _entries.Clear();
            EndEpisode();
        }
    }

    private void EndEpisode()
    {
        _tried.Clear();
        _holdSince = null;
    }

    private static void Narrow(QuarantineEntry e, IServerProbe probe)
    {
        if (e.Suspect.Kind != SuspectKind.Candidates)
        {
            return;
        }

        var hung = e.Suspect.Servers.Where(s => s.CreationTime != ServerIdentity.AlreadyEnded && probe.IsHung(s)).ToList();
        if (hung.Count == 1)
        {
            e.Suspect = Suspect.KnownServer(hung[0]);            // exactly one candidate shows hang evidence (04 § M.4)
        }
    }
}
