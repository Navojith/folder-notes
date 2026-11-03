namespace FolderNotes.App.Explorer;

/// <summary>What the worker is doing when it calls out of process (04 § M.2).</summary>
internal enum CallSite
{
    None,

    /// <summary><c>CoCreateInstance(ShellWindows)</c> and wrapping the root. Target: the shell (probable, L7).</summary>
    RootCreate,

    /// <summary><c>get_Count</c> and the items of one enumeration. Target: the shell while no item is open.</summary>
    Enumerate,

    /// <summary><c>Item(i)</c>, the cast and <c>get_HWND</c> of item i. Target: unknown (no documented API names a proxy's server).</summary>
    ItemProbe,

    /// <summary><c>QueryService</c>, <c>GetWindow</c> and the wraps of item i, after its frame is known. Target: the frame's process.</summary>
    ItemRead,

    /// <summary>Reading one tab's folder. Target: the foreground frame's process.</summary>
    PathRead,

    /// <summary>Releasing proxies (map sweeps and drops, disposal). Target: unknown (may span several processes).</summary>
    Release,
}

/// <summary>One operation as written by the worker. <see cref="TargetPid"/> 0 = not known.</summary>
internal readonly record struct CallRecord(int Generation, long OperationId, CallSite Site, uint TargetPid, nint FrameHwnd, int ItemIndex, long StartedAt);

internal enum JournalReadKind
{
    /// <summary>No operation is open: the worker is not calling out of process.</summary>
    Idle,

    /// <summary>An operation is open and the record passed every check (04 § M.2).</summary>
    Active,

    /// <summary>A record was being written or changed while it was read: it says nothing.</summary>
    Indeterminate,

    /// <summary>An open record that cannot belong to the evaluation being timed (older than it, or another generation).</summary>
    Stale,
}

/// <summary>A consistent read of the journal: the innermost active operation, or why there is none.</summary>
internal readonly record struct JournalSnapshot(JournalReadKind Kind, CallRecord Record)
{
    public static JournalSnapshot Idle { get; } = new(JournalReadKind.Idle, default);

    public bool IsActive => Kind == JournalReadKind.Active;
}

/// <summary>
/// The per-worker record of the operation in flight (04 § M.2). Written <b>only by the worker thread</b>; read by the
/// watchdog/supervisor at any time. Two nesting levels (the engine's operation, and the gateway's per-item operation inside
/// it); a read reports the innermost active one. Each level is a sequence lock: the version is odd while it is written, and a
/// read that sees an odd or changed version is <see cref="JournalReadKind.Indeterminate"/>, so a torn record is never taken
/// for an active call. A record is cleared only by closing its operation, after its calls returned.
/// </summary>
internal sealed class CallJournal
{
    private const int Levels = 2;
    private const int ReadAttempts = 4;

    private readonly Slot[] _slots = [new(), new()];
    private readonly TimeProvider _clock;
    private long _nextOperation;
    private int _defects;

    public CallJournal(int generation, TimeProvider? clock = null)
    {
        Generation = generation;
        _clock = clock ?? TimeProvider.System;
    }

    public int Generation { get; }

    /// <summary>Operations found still open at the start of an evaluation, or opened beyond the nesting depth (a defect; stats show it).</summary>
    public int Defects => Volatile.Read(ref _defects);

    /// <summary>The highest operation id handed out so far (0 = none).</summary>
    public long LastOperationId => Volatile.Read(ref _nextOperation);

    /// <summary>Test seam: runs while a record's version is odd (mid-write), so a test can read a torn record.</summary>
    internal Action? WhileWriting { get; set; }

    /// <summary>
    /// Worker thread: opens an operation before its first outgoing call and returns its id. Close it in a <c>finally</c>
    /// after the last call returned. Prefer <see cref="Scope"/>.
    /// </summary>
    public long Open(CallSite site, uint targetPid = 0, nint frame = 0, int itemIndex = -1)
    {
        long id = Interlocked.Increment(ref _nextOperation);
        int level = Array.FindIndex(_slots, s => !s.Active);
        if (level < 0)
        {
            Interlocked.Increment(ref _defects);         // deeper than two: overwrite the inner level, never lose the outer one
            level = Levels - 1;
        }

        Write(_slots[level], active: true, new CallRecord(Generation, id, site, targetPid, frame, itemIndex, _clock.GetTimestamp()));
        return id;
    }

    /// <summary>Worker thread: the operation's calls have all returned.</summary>
    public void Close(long operationId)
    {
        foreach (var slot in _slots)
        {
            if (slot.Active && slot.Record.OperationId == operationId)
            {
                Write(slot, active: false, slot.Record);
                return;
            }
        }
    }

    /// <summary>Worker thread: an <c>using</c>-scope for one operation.</summary>
    public OperationScope Scope(CallSite site, uint targetPid = 0, nint frame = 0, int itemIndex = -1) =>
        new(this, Open(site, targetPid, frame, itemIndex));

    /// <summary>
    /// Worker thread, at the start of every watchdog-timed evaluation: nothing may be open between evaluations. A dangling
    /// operation is closed and counted as a defect (04 § M.2).
    /// </summary>
    public void BeginEvaluation()
    {
        foreach (var slot in _slots)
        {
            if (slot.Active)
            {
                Interlocked.Increment(ref _defects);
                Write(slot, active: false, slot.Record);
            }
        }
    }

    /// <summary>
    /// Any thread: the innermost active operation of the evaluation that started at <paramref name="evaluationStartedAt"/>
    /// (a <see cref="TimeProvider"/> timestamp; 0 = do not check the age). Torn reads are Indeterminate; a record older than the
    /// evaluation, or of another generation, is Stale.
    /// </summary>
    public JournalSnapshot Read(long evaluationStartedAt = 0)
    {
        JournalSnapshot? innermost = null;
        foreach (var slot in _slots)
        {
            if (!TryRead(slot, out bool active, out var record))
            {
                return new JournalSnapshot(JournalReadKind.Indeterminate, default);
            }

            if (!active)
            {
                continue;
            }

            var kind = record.Generation != Generation || (evaluationStartedAt != 0 && record.StartedAt < evaluationStartedAt)
                ? JournalReadKind.Stale
                : JournalReadKind.Active;
            innermost = new JournalSnapshot(kind, record);
        }

        return innermost ?? JournalSnapshot.Idle;
    }

    /// <summary>
    /// Any thread: true once operation <paramref name="operationId"/> is no longer open (evidence E1, 04 § M.4). A torn read
    /// answers false (not yet known), never true.
    /// </summary>
    public bool HasCompleted(long operationId)
    {
        if (operationId <= 0 || operationId > LastOperationId)
        {
            return false;
        }

        foreach (var slot in _slots)
        {
            if (!TryRead(slot, out bool active, out var record))
            {
                return false;
            }

            if (active && record.OperationId == operationId)
            {
                return false;
            }
        }

        return true;
    }

    private void Write(Slot slot, bool active, CallRecord record)
    {
        Interlocked.Increment(ref slot.Version);         // odd: being written (a full fence, so the fields cannot move above it)
        WhileWriting?.Invoke();
        slot.Record = record;
        slot.Active = active;
        Interlocked.Increment(ref slot.Version);         // even: consistent again
    }

    private static bool TryRead(Slot slot, out bool active, out CallRecord record)
    {
        for (int attempt = 0; attempt < ReadAttempts; attempt++)
        {
            int before = Volatile.Read(ref slot.Version);
            if ((before & 1) != 0)
            {
                continue;
            }

            active = slot.Active;
            record = slot.Record;
            Interlocked.MemoryBarrier();
            if (Volatile.Read(ref slot.Version) == before)
            {
                return true;
            }
        }

        active = false;
        record = default;
        return false;
    }

    private sealed class Slot
    {
        public int Version;
        public bool Active;
        public CallRecord Record;
    }

    /// <summary>Closes its operation when disposed (worker thread).</summary>
    internal readonly struct OperationScope(CallJournal? journal, long operationId) : IDisposable
    {
        public long OperationId { get; } = operationId;

        public void Dispose() => journal?.Close(OperationId);
    }
}
