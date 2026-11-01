namespace FolderNotes.App.Explorer;

internal enum WatchdogAction
{
    None,

    /// <summary>Call <c>CoCancelCall</c> on the worker thread (issued once per evaluation).</summary>
    Cancel,

    /// <summary>The cancel did not free the thread in time: declare it wedged and abandon it.</summary>
    Abandon,
}

internal sealed record WatchdogOptions
{
    public static WatchdogOptions Default { get; } = new();

    /// <summary>Evaluation runtime after which the blocked call is cancelled (04 B.6: 250 ms).</summary>
    public TimeSpan CancelAfter { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Time after the cancel was issued at which the thread is abandoned (04 B.6: 1 s).</summary>
    public TimeSpan AbandonAfterCancel { get; init; } = TimeSpan.FromSeconds(1);
}

/// <summary>
/// The time-boxing rules of 04 B.6 as a pure state machine: "cancel at 250 ms into an evaluation, abandon 1 s
/// after the cancel". The watchdog thread polls <see cref="Check"/> (sleeping for <see cref="TimeUntilNextCheck"/>);
/// the worker brackets each evaluation with <see cref="Begin"/> / <see cref="End"/>. Thread-safe. Boundaries are
/// inclusive: exactly at the limit the action fires.
/// </summary>
internal sealed class EvaluationWatchdog
{
    private readonly Lock _gate = new();
    private readonly TimeProvider _clock;
    private readonly WatchdogOptions _o;
    private long? _startedAt;
    private long? _cancelIssuedAt;
    private bool _abandoned;

    public EvaluationWatchdog(TimeProvider clock, WatchdogOptions? options = null)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _o = options ?? WatchdogOptions.Default;
    }

    /// <summary>The worker is about to make COM calls. Ignored once the worker has been abandoned.</summary>
    public void Begin()
    {
        lock (_gate)
        {
            if (!_abandoned)
            {
                _startedAt = _clock.GetTimestamp();
                _cancelIssuedAt = null;
            }
        }
    }

    /// <summary>The evaluation returned (normally or cancelled).</summary>
    public void End()
    {
        lock (_gate)
        {
            _startedAt = null;
            _cancelIssuedAt = null;
        }
    }

    /// <summary>True once <see cref="WatchdogAction.Abandon"/> has been returned: the worker is never trusted again.</summary>
    public bool Abandoned
    {
        get { lock (_gate) { return _abandoned; } }
    }

    public WatchdogAction Check()
    {
        lock (_gate)
        {
            if (_abandoned || _startedAt is not { } started)
            {
                return WatchdogAction.None;
            }

            var now = _clock.GetTimestamp();
            if (_cancelIssuedAt is { } cancelled)
            {
                if (_clock.GetElapsedTime(cancelled, now) >= _o.AbandonAfterCancel)
                {
                    _abandoned = true;
                    return WatchdogAction.Abandon;
                }

                return WatchdogAction.None;
            }

            if (_clock.GetElapsedTime(started, now) >= _o.CancelAfter)
            {
                _cancelIssuedAt = now;
                return WatchdogAction.Cancel;
            }

            return WatchdogAction.None;
        }
    }

    /// <summary>How long the watchdog thread may sleep before the next possible action; null = wait for <see cref="Begin"/>.</summary>
    public TimeSpan? TimeUntilNextCheck()
    {
        lock (_gate)
        {
            if (_abandoned || _startedAt is not { } started)
            {
                return null;
            }

            var now = _clock.GetTimestamp();
            var due = _cancelIssuedAt is { } cancelled
                ? _o.AbandonAfterCancel - _clock.GetElapsedTime(cancelled, now)
                : _o.CancelAfter - _clock.GetElapsedTime(started, now);
            return due > TimeSpan.Zero ? due : TimeSpan.Zero;
        }
    }
}
