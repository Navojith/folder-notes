namespace FolderNotes.App.Explorer;

[Flags]
internal enum SignalKinds
{
    None = 0,
    Foreground = 1,
    Navigation = 2,   // NAMECHANGE / SHOW / HIDE / MINIMIZE
    Structure = 4,    // CREATE / DESTROY of a frame or tab
}

internal enum EvaluationReason
{
    /// <summary>Leading edge: the first signal after a quiet period.</summary>
    Signal,

    /// <summary>Trailing edge: signals arrived inside the throttle window (04 D.3).</summary>
    Trailing,

    /// <summary>One more read after a changed state, to catch Explorer's transient states (04 D.3).</summary>
    Confirm,

    /// <summary>A rung of the retry ladder (04 D.4).</summary>
    Retry,

    /// <summary>The safety-net poll (04 D.5).</summary>
    Poll,

    /// <summary>The slow map reconciliation (04 C).</summary>
    Reconcile,
}

internal enum EvaluationResultKind
{
    /// <summary>A folder (or "no folder") was determined.</summary>
    Resolved,

    /// <summary>The tab could not be matched or read (a miss that a rebuild did not fix).</summary>
    Unresolved,

    /// <summary>A transient COM failure.</summary>
    Transient,
}

internal readonly record struct EvaluationRequest(long Sequence, EvaluationReason Reason, SignalKinds Signals, bool Rebuild, int RetryAttempt);

/// <param name="ExplorerForeground">An Explorer frame (not minimized) is foreground: timers run only then.</param>
/// <param name="StateChanged">The published state changed because of this evaluation (arms the confirm pass).</param>
internal readonly record struct EvaluationOutcome(bool ExplorerForeground, bool StateChanged, EvaluationResultKind Kind);

/// <param name="LadderExhausted">The last retry rung failed too: the host may mark the frame unresolved (04 G, L8).</param>
internal readonly record struct CompletionAdvice(bool LadderExhausted);

internal sealed record SchedulerOptions
{
    public static SchedulerOptions Default { get; } = new();

    public TimeSpan ThrottleWindow { get; init; } = TimeSpan.FromMilliseconds(30);

    public TimeSpan ConfirmDelay { get; init; } = TimeSpan.FromMilliseconds(75);

    /// <summary>Offsets from the start of the first failing evaluation (04 D.4: +50, +150, +400, +1000 ms).</summary>
    public IReadOnlyList<TimeSpan> RetryOffsets { get; init; } =
        [TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(1000)];

    public TimeSpan PollFast { get; init; } = TimeSpan.FromMilliseconds(500);

    public TimeSpan PollSlow { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Silence after which the poll backs off from fast to slow.</summary>
    public TimeSpan PollBackoffAfter { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan ReconcileEvery { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// The coalescing and timing rules of 04 § ExplorerTracker design D, as a pure state machine. It owns no timer
/// and no thread: the host asks <see cref="TimeUntilNextDue"/> how long to sleep, calls <see cref="TryTakeDue"/>
/// when it wakes, runs the evaluation, and reports <see cref="Complete"/>. All time comes from a
/// <see cref="TimeProvider"/> (monotonic timestamps), so tests use a fake clock. Not thread-safe: one loop thread.
/// <para>
/// Throttle (D.3): the first signal after quiet is due at once (leading edge). Every evaluation opens a window
/// (30 ms); signals inside it only mark dirty and produce exactly one trailing evaluation when it ends, so the
/// last state is always read and a continuous stream costs at most one evaluation per window. Confirm (+75 ms
/// after a changed state, never chained), the retry ladder, the poll and the reconcile are other due times that
/// respect the same window. While no Explorer frame is foreground nothing but a pending signal is ever due.
/// </para>
/// </summary>
internal sealed class EvaluationScheduler
{
    private readonly TimeProvider _clock;
    private readonly SchedulerOptions _o;
    private readonly Func<long> _nextSequence;
    private readonly long _t0;

    private TimeSpan? _windowEnd;
    private TimeSpan? _pendingAt;
    private bool _pendingIsTrailing;
    private SignalKinds _dirty;
    private TimeSpan? _confirmAt;
    private TimeSpan? _retryAt;
    private int _retryIndex;
    private TimeSpan _ladderStart;
    private TimeSpan? _pollAt;
    private TimeSpan? _reconcileAt;
    private TimeSpan _lastSignalAt;
    private EvaluationRequest? _inFlight;
    private TimeSpan _inFlightStart;

    public EvaluationScheduler(TimeProvider clock, SchedulerOptions? options, Func<long> nextSequence)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _o = options ?? SchedulerOptions.Default;
        _nextSequence = nextSequence ?? throw new ArgumentNullException(nameof(nextSequence));
        _t0 = _clock.GetTimestamp();
        _lastSignalAt = TimeSpan.Zero;
    }

    /// <summary>An Explorer frame is foreground (from the last completed evaluation).</summary>
    public bool ExplorerForeground { get; private set; }

    public bool EvaluationInFlight => _inFlight is not null;

    private TimeSpan Now => _clock.GetElapsedTime(_t0);

    /// <summary>A WinEvent (already reduced to a kind) arrived. Safe to call at any moment on the loop thread.</summary>
    public void Signal(SignalKinds kind)
    {
        var now = Now;
        _lastSignalAt = now;
        _dirty |= kind;
        bool inWindow = _windowEnd is { } we && now < we;
        var candidate = inWindow ? _windowEnd!.Value : now;
        if (_pendingAt is null || candidate < _pendingAt)
        {
            _pendingAt = candidate;
            _pendingIsTrailing = inWindow;
        }

        if (ExplorerForeground)
        {
            _pollAt = now + _o.PollFast; // activity: poll fast again
        }
    }

    /// <summary>How long until the next evaluation is due (zero when due now); null when nothing is scheduled.</summary>
    public TimeSpan? TimeUntilNextDue()
    {
        if (_inFlight is not null)
        {
            return null;
        }

        var now = Now;
        TimeSpan? best = null;
        void Consider(TimeSpan? at, bool respectWindow)
        {
            if (at is not { } t)
            {
                return;
            }

            if (respectWindow && _windowEnd is { } we && t < we)
            {
                t = we;
            }

            if (best is null || t < best)
            {
                best = t;
            }
        }

        Consider(_pendingAt, respectWindow: false);
        if (ExplorerForeground)
        {
            Consider(_confirmAt, true);
            Consider(_retryAt, true);
            Consider(_reconcileAt, true);
            Consider(_pollAt, true);
        }

        return best is { } b ? (b > now ? b - now : TimeSpan.Zero) : null;
    }

    /// <summary>Takes the next due evaluation, if any. At most one is in flight at a time.</summary>
    public bool TryTakeDue(out EvaluationRequest request)
    {
        request = default;
        if (_inFlight is not null)
        {
            return false;
        }

        var now = Now;
        var inWindow = _windowEnd is { } we && now < we;
        EvaluationReason reason;
        if (_pendingAt is { } p && p <= now)
        {
            reason = _pendingIsTrailing ? EvaluationReason.Trailing : EvaluationReason.Signal;
        }
        else if (ExplorerForeground && !inWindow)
        {
            if (_confirmAt is { } c && c <= now)
            {
                reason = EvaluationReason.Confirm;
            }
            else if (_retryAt is { } r && r <= now)
            {
                reason = EvaluationReason.Retry;
            }
            else if (_reconcileAt is { } rc && rc <= now)
            {
                reason = EvaluationReason.Reconcile;
            }
            else if (_pollAt is { } pl && pl <= now)
            {
                reason = EvaluationReason.Poll;
            }
            else
            {
                return false;
            }
        }
        else
        {
            return false;
        }

        bool rebuild = ExplorerForeground && _reconcileAt is { } due && due <= now;
        request = new EvaluationRequest(_nextSequence(), reason, _dirty, rebuild, reason == EvaluationReason.Retry ? _retryIndex : 0);

        // This read satisfies everything that was waiting for one.
        _pendingAt = null;
        _pendingIsTrailing = false;
        _dirty = SignalKinds.None;
        _confirmAt = null;
        if (reason is not (EvaluationReason.Poll or EvaluationReason.Reconcile))
        {
            _retryAt = null; // a poll or reconcile does not take over a pending ladder rung (04 D.4)
        }

        _windowEnd = now + _o.ThrottleWindow;
        _inFlight = request;
        _inFlightStart = now;
        return true;
    }

    /// <summary>Reports the result of the evaluation taken with <see cref="TryTakeDue"/>.</summary>
    public CompletionAdvice Complete(long sequence, EvaluationOutcome outcome)
    {
        if (_inFlight is not { } req || req.Sequence != sequence)
        {
            return default; // not the evaluation in flight (e.g. from an abandoned worker): ignore
        }

        _inFlight = null;
        var now = Now;
        bool wasForeground = ExplorerForeground;
        ExplorerForeground = outcome.ExplorerForeground;

        if (!ExplorerForeground)
        {
            // Background: no timers at all (04 D.5, F idle CPU). A pending signal is still honoured.
            _confirmAt = _retryAt = _pollAt = _reconcileAt = null;
            _retryIndex = 0;
            return default;
        }

        if (!wasForeground || req.Rebuild)
        {
            _reconcileAt = now + _o.ReconcileEvery;
        }

        _pollAt = now + PollInterval(now);

        if (outcome.Kind == EvaluationResultKind.Resolved)
        {
            _retryIndex = 0;
            _retryAt = null; // a good read ends any ladder
            if (outcome.StateChanged && req.Reason != EvaluationReason.Confirm)
            {
                _confirmAt = now + _o.ConfirmDelay; // a confirm never chains another confirm
            }

            return default;
        }

        // Unresolved or transient: climb the ladder (04 D.4). A signal-driven failure starts a new ladder; a failing
        // poll or reconcile leaves the ladder as it is, so "then stop until the next signal" holds and a persistent
        // failure costs one read per poll interval, not a ladder per poll.
        if (req.Reason is EvaluationReason.Poll or EvaluationReason.Reconcile)
        {
            return default;
        }

        if (req.Reason == EvaluationReason.Retry)
        {
            _retryIndex++;
        }
        else
        {
            _retryIndex = 0;
            _ladderStart = _inFlightStart;
        }

        if (_retryIndex < _o.RetryOffsets.Count)
        {
            var at = _ladderStart + _o.RetryOffsets[_retryIndex];
            _retryAt = at > now ? at : now;
            return default;
        }

        _retryAt = null;
        return new CompletionAdvice(LadderExhausted: true);
    }

    private TimeSpan PollInterval(TimeSpan now) =>
        now - _lastSignalAt < _o.PollBackoffAfter ? _o.PollFast : _o.PollSlow;
}
