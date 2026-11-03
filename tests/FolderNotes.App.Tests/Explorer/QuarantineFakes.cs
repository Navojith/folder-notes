using FolderNotes.App.Explorer;

namespace FolderNotes.App.Tests.Explorer;

/// <summary>An abandoned worker as quarantine sees it (E1): the test decides when its operation completes or its thread ends.</summary>
internal sealed class FakeZombie : IAbandonedWorkerView
{
    private readonly HashSet<long> _completed = [];
    private volatile bool _exited;

    public bool HasExited => _exited;

    public void Exit() => _exited = true;

    public void Complete(long operationId)
    {
        lock (_completed)
        {
            _completed.Add(operationId);
        }
    }

    public bool HasCompleted(long operationId)
    {
        lock (_completed)
        {
            return _completed.Contains(operationId);
        }
    }
}

/// <summary>A scriptable set of Explorer processes for the monitor: running, exited, replaced (pid reuse), hung. Thread-safe.</summary>
internal sealed class FakeServerProbe : IServerProbe
{
    private readonly Lock _gate = new();
    private readonly Dictionary<uint, long> _running = [];
    private readonly HashSet<uint> _hung = [];
    private readonly List<uint> _framePids = [];
    private long _nextCreation = 1000;

    public uint ShellPid { get; set; }

    public int Looks;

    /// <summary>Starts a process (a new creation time each time, so a reused pid is a different identity).</summary>
    public ServerIdentity Run(uint pid, bool ownsFrame = true)
    {
        lock (_gate)
        {
            _running[pid] = ++_nextCreation;
            if (ownsFrame && !_framePids.Contains(pid))
            {
                _framePids.Add(pid);
            }

            return new ServerIdentity(pid, _running[pid]);
        }
    }

    public void Exit(uint pid)
    {
        lock (_gate)
        {
            _running.Remove(pid);
            _framePids.Remove(pid);
            _hung.Remove(pid);
        }
    }

    public void Hang(uint pid, bool hung = true)
    {
        lock (_gate)
        {
            if (hung)
            {
                _hung.Add(pid);
            }
            else
            {
                _hung.Remove(pid);
            }
        }
    }

    public ServerIdentity? Identify(uint pid)
    {
        lock (_gate)
        {
            Looks++;
            return _running.TryGetValue(pid, out long c) ? new ServerIdentity(pid, c) : null;
        }
    }

    public bool IsRunning(ServerIdentity server)
    {
        lock (_gate)
        {
            return _running.TryGetValue(server.Pid, out long c) && c == server.CreationTime;
        }
    }

    public IReadOnlyList<uint> ExplorerFrameProcesses()
    {
        lock (_gate)
        {
            return [.. _framePids];
        }
    }

    public uint ShellProcessId() => ShellPid;

    public bool IsHung(ServerIdentity server)
    {
        lock (_gate)
        {
            return _running.TryGetValue(server.Pid, out long c) && c == server.CreationTime && _hung.Contains(server.Pid);
        }
    }

    public bool AnyExplorerFrameHung()
    {
        lock (_gate)
        {
            return _hung.Count > 0;
        }
    }
}

/// <summary>Builds quarantine entries the way the supervisor does, for the pure tests.</summary>
internal static class TestEntries
{
    private static long _op;

    public static JournalSnapshot ActiveSnapshot(CallSite site, uint pid, long startedAt = 0) =>
        new(JournalReadKind.Active, new CallRecord(1, Interlocked.Increment(ref _op), site, pid, 0, -1, startedAt));

    /// <summary>An abandonment whose blocked operation targeted a known server.</summary>
    public static QuarantineEntry Known(FakeZombie zombie, ServerIdentity server, TimeProvider clock, int? cancelHr = 0) =>
        new(zombie, ActiveSnapshot(CallSite.PathRead, server.Pid), Suspect.KnownServer(server), cancelHr, clock.GetTimestamp());

    /// <summary>An abandonment with no usable journal record.</summary>
    public static QuarantineEntry Unknown(FakeZombie zombie, TimeProvider clock) =>
        new(zombie, JournalSnapshot.Idle, Suspect.Unknown, null, clock.GetTimestamp());
}
