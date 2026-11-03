using System.Runtime.InteropServices.Marshalling;

namespace FolderNotes.App.Explorer.Com;

/// <summary>
/// Counts live <see cref="ComRef{T}"/> objects (04 B.3: "a live-RCW counter, always on; it is one integer").
/// Tests assert it returns to 0, and that nothing was ever released from a thread other than the one that
/// acquired it. One tracker per gateway, so parallel tests do not see each other.
/// </summary>
internal sealed class RcwTracker
{
    private int _live;
    private int _crossThreadReleases;
    private long _created;

    public int Live => Volatile.Read(ref _live);

    public long Created => Interlocked.Read(ref _created);

    /// <summary>Releases that ran on a different thread than the acquiring one. Must stay 0 (04 B.2).</summary>
    public int CrossThreadReleases => Volatile.Read(ref _crossThreadReleases);

    internal void Added()
    {
        Interlocked.Increment(ref _live);
        Interlocked.Increment(ref _created);
    }

    internal void Removed(bool crossThread)
    {
        Interlocked.Decrement(ref _live);
        if (crossThread)
        {
            Interlocked.Increment(ref _crossThreadReleases);
        }
    }
}

/// <summary>
/// Owner of one source-generated COM wrapper (04 B.3). <see cref="Dispose"/> calls
/// <see cref="ComObject.FinalRelease"/> (the only working release for generated RCWs, S5/P1:
/// <c>Marshal.ReleaseComObject</c> throws on them) and is idempotent. Created, used and disposed on the COM worker thread.
/// </summary>
internal sealed class ComRef<T> : IDisposable
    where T : class
{
    private readonly RcwTracker _tracker;
    private readonly int _ownerThread;
    private T? _value;

    public ComRef(T value, RcwTracker tracker)
    {
        _value = value ?? throw new ArgumentNullException(nameof(value));
        _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
        _ownerThread = Environment.CurrentManagedThreadId;
        _tracker.Added();
    }

    public bool IsReleased => Volatile.Read(ref _value) is null;

    public T Value => Volatile.Read(ref _value) ?? throw new ObjectDisposedException(nameof(ComRef<T>));

    public void Dispose()
    {
        var value = Interlocked.Exchange(ref _value, null);
        if (value is null)
        {
            return;
        }

        try
        {
            if (value is ComObject com)
            {
                com.FinalRelease();
            }
        }
        catch
        {
            // a dead proxy can fail to release; swallowing is correct (04 F: "dead proxies release fast; failures swallowed")
        }
        finally
        {
            _tracker.Removed(crossThread: Environment.CurrentManagedThreadId != _ownerThread);
        }
    }
}
