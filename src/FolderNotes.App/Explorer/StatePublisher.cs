namespace FolderNotes.App.Explorer;

internal enum PublishResult
{
    Published,

    /// <summary>Same frame and path as the last published state (04 D.6): nothing is delivered.</summary>
    Unchanged,

    /// <summary>The caller belongs to an abandoned worker generation: nothing is delivered, ever.</summary>
    StaleGeneration,

    /// <summary>A newer evaluation has already been seen: an older result must never overwrite it.</summary>
    StaleSequence,
}

/// <summary>
/// The one gate every state passes through (04 D.6, D.7b, B.6). It enforces, atomically with delivery:
/// <list type="bullet">
/// <item>an abandoned worker's generation can never publish (the generation is bumped before any replacement exists);</item>
/// <item>an evaluation only counts if its sequence is newer than every one already seen, published or not, so an
/// older result finishing late (or after a no-op or failed newer one) is dropped;</item>
/// <item>an unchanged (frame, path) is not delivered again.</item>
/// </list>
/// Sequences are taken when an evaluation <i>starts</i> (<see cref="NextSequence"/>). The sink is called while the
/// gate's lock is held so that "abandon" and "deliver" cannot interleave; it must only enqueue (latest-wins) and
/// must not block or call back into the publisher. Thread-safe: the watchdog thread calls <see cref="Abandon"/>.
/// </summary>
internal sealed class StatePublisher
{
    private readonly Lock _gate = new();
    private readonly Action<ActiveFolderState> _sink;
    private long _sequence;
    private long _highestSeen;
    private int _generation;
    private ActiveFolderState _current = ActiveFolderState.Inactive();

    public StatePublisher(Action<ActiveFolderState> sink)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
    }

    public int Generation
    {
        get { lock (_gate) { return _generation; } }
    }

    /// <summary>The last state delivered (initially Inactive).</summary>
    public ActiveFolderState Current
    {
        get { lock (_gate) { return _current; } }
    }

    /// <summary>A new, strictly increasing evaluation sequence (shared by every generation).</summary>
    public long NextSequence() => Interlocked.Increment(ref _sequence);

    /// <summary>
    /// Offers the result of an evaluation. <paramref name="reading"/> is null when the evaluation produced no
    /// information (a transient failure): it still advances "newest seen" so an older result cannot slip past it.
    /// </summary>
    public PublishResult TryPublish(int generation, long sequence, ActiveFolderState? reading)
    {
        lock (_gate)
        {
            if (generation != _generation)
            {
                return PublishResult.StaleGeneration;
            }

            if (sequence <= _highestSeen)
            {
                return PublishResult.StaleSequence;
            }

            _highestSeen = sequence;
            if (reading is not { } r)
            {
                return PublishResult.Unchanged;
            }

            var next = r with { Sequence = sequence };
            if (next.SameFolderAs(_current))
            {
                return PublishResult.Unchanged;
            }

            _current = next;
            _sink(next);
            return PublishResult.Published;
        }
    }

    /// <summary>
    /// Invalidates the current worker generation and publishes Inactive (a wedge, 04 B.6). After this returns,
    /// nothing the old worker produces can be delivered. Returns the generation the replacement must use.
    /// </summary>
    public int Abandon()
    {
        lock (_gate)
        {
            _generation++;
            PublishInactiveLocked();
            return _generation;
        }
    }

    /// <summary>Publishes Inactive from the current generation (stop, shell gone, a long-lived Explorer-less state).</summary>
    public void PublishInactive()
    {
        lock (_gate)
        {
            PublishInactiveLocked();
        }
    }

    private void PublishInactiveLocked()
    {
        long seq = NextSequence();
        _highestSeen = Math.Max(_highestSeen, seq);
        if (_current.IsInactive)
        {
            return;
        }

        _current = ActiveFolderState.Inactive(seq);
        _sink(_current);
    }
}
