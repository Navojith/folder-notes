namespace FolderNotes.App.Explorer;

/// <summary>What happened, as the hook thread saw it. One bit per kind; the worker decides what each means (04 D).</summary>
[Flags]
internal enum TrackerSignal
{
    None = 0,
    Foreground = 1,
    NameChange = 2,
    ShowHide = 4,
    Minimize = 8,
    CreateDestroy = 16,
}

/// <summary>Posts exactly one wake-up message to a worker's queue (04 B.7). Implemented over a thread id in production.</summary>
internal interface IWakeTarget
{
    /// <summary>True if the wake-up was queued. Must be cheap and must not block. A false result is dropped silently.</summary>
    bool TryPost();
}

/// <summary>
/// The only thing the hook callbacks touch (04 B.7): a pending-signal word and the <i>current</i> worker's wake-up
/// target. It decouples the hooks from the workers, so replacing an abandoned worker is <see cref="Detach"/> then
/// <see cref="Attach"/> and never touches a hook.
/// <para>
/// Guarantees: (1) <see cref="Raise"/> is lock-free, allocation-free and non-blocking; (2) at most one wake-up is
/// outstanding per attached worker; (3) a bit that was raised is never lost: the consumer clears the
/// "wake-up outstanding" flag <i>before</i> it takes the bits, so a raise racing with a consume either lands in the
/// bits taken or posts a new wake-up; (4) a detached or replaced worker can neither receive wake-ups nor take bits.
/// </para>
/// </summary>
internal sealed class SignalRouter
{
    private int _pending;
    private int _wakeOutstanding;
    private IWakeTarget? _target;
    private long _wakeUpsPosted;
    private long _raised;

    /// <summary>Test seam: runs between the two steps of <see cref="Consume"/>, i.e. exactly where a racing raise can land. Null in production.</summary>
    internal Action? GapInConsume { get; set; }

    /// <summary>Test seam: runs between recording the bit and trying to post in <see cref="Raise"/>. Null in production.</summary>
    internal Action? GapInRaise { get; set; }

    /// <summary>Called by hook callbacks. Records the bit and, if no wake-up is outstanding, posts one.</summary>
    public void Raise(TrackerSignal signal)
    {
        Interlocked.Or(ref _pending, (int)signal);
        Interlocked.Increment(ref _raised);
        GapInRaise?.Invoke();
        Kick();
    }

    /// <summary>Make <paramref name="target"/> the one worker that receives wake-ups; bits raised meanwhile wake it at once.</summary>
    public void Attach(IWakeTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        Volatile.Write(ref _target, target);
        Interlocked.Exchange(ref _wakeOutstanding, 0);   // a wake-up the previous worker never read is void
        Kick();
    }

    /// <summary>Stop sending to <paramref name="target"/> if it is still the current one (a stale call is a no-op).</summary>
    public bool Detach(IWakeTarget target)
    {
        if (Interlocked.CompareExchange(ref _target, null, target) != target)
        {
            return false;
        }

        Interlocked.Exchange(ref _wakeOutstanding, 0);
        return true;
    }

    public bool IsAttached => Volatile.Read(ref _target) is not null;

    /// <summary>
    /// The worker's handler for a wake-up. Returns and clears the pending bits, or <see cref="TrackerSignal.None"/>
    /// when <paramref name="caller"/> is not the current worker (an abandoned worker waking up late takes nothing).
    /// </summary>
    public TrackerSignal Consume(IWakeTarget caller)
    {
        if (!ReferenceEquals(Volatile.Read(ref _target), caller))
        {
            return TrackerSignal.None;
        }

        Interlocked.Exchange(ref _wakeOutstanding, 0);    // first: a later raise posts again
        GapInConsume?.Invoke();
        return (TrackerSignal)Interlocked.Exchange(ref _pending, 0);
    }

    /// <summary>Bits waiting to be consumed (diagnostics and tests).</summary>
    public TrackerSignal Pending => (TrackerSignal)Volatile.Read(ref _pending);

    public long WakeUpsPosted => Interlocked.Read(ref _wakeUpsPosted);

    public long SignalsRaised => Interlocked.Read(ref _raised);

    private void Kick()
    {
        var target = Volatile.Read(ref _target);
        if (target is null || Volatile.Read(ref _pending) == 0)
        {
            return;                                        // no worker yet: bits wait for Attach
        }

        if (Interlocked.CompareExchange(ref _wakeOutstanding, 1, 0) != 0)
        {
            return;                                        // one is already on its way
        }

        bool posted;
        try
        {
            posted = target.TryPost();
        }
        catch
        {
            posted = false;                                // a callback must never throw
        }

        if (posted)
        {
            Interlocked.Increment(ref _wakeUpsPosted);
        }
        else
        {
            Interlocked.Exchange(ref _wakeOutstanding, 0); // the next raise (or the poll) tries again
        }
    }
}
