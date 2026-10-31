using Microsoft.Data.Sqlite;

namespace FolderNotes.Core.Storage;

/// <summary>The serialized writer (ADR-011) and the read helper.</summary>
public sealed partial class SqliteNoteStore
{
    /// <summary>The transaction a queued write runs in, plus the events to raise after commit.</summary>
    internal sealed class WriteContext(SqliteNoteStore store, SqliteConnection connection, SqliteTransaction transaction)
    {
        private readonly List<NoteChangedEventArgs> _events = [];

        public SqliteNoteStore Store { get; } = store;
        public SqliteConnection Connection { get; } = connection;
        public SqliteTransaction Transaction { get; } = transaction;
        public bool LabelsChanged { get; set; }
        public IReadOnlyList<NoteChangedEventArgs> Events => _events;

        public SqliteCommand Command(string sql)
        {
            var cmd = Connection.CreateCommand();
            cmd.Transaction = Transaction;
            cmd.CommandText = sql;
            return cmd;
        }

        public void Raise(Guid noteId, NoteChangeKind kind, string pathNorm) => _events.Add(new(noteId, kind, pathNorm));

        /// <summary>For work rolled back to a savepoint: its events must not be raised.</summary>
        public void DropEventsFor(Guid noteId) => _events.RemoveAll(e => e.NoteId == noteId);
    }

    private abstract class WriteOp
    {
        public abstract void Run(SqliteNoteStore store);

        public abstract void Cancel();
    }

    private sealed class WriteOp<T>(Func<WriteContext, T> work, CancellationToken ct) : WriteOp
    {
        public TaskCompletionSource<T> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override void Cancel() => Completion.TrySetCanceled();

        public override void Run(SqliteNoteStore store)
        {
            if (ct.IsCancellationRequested)
            {
                Completion.TrySetCanceled(ct);
                return;
            }

            WriteContext ctx;
            T result;
            try
            {
                using var tx = store._writer.BeginTransaction(deferred: false);
                ctx = new WriteContext(store, store._writer, tx);
                result = work(ctx);
                tx.Commit();
            }
            catch (Exception ex)
            {
                Completion.TrySetException(ex);
                return;
            }

            store.RaiseEvents(ctx);
            Completion.TrySetResult(result);
        }
    }

    /// <summary>Runs <paramref name="work"/> in its own transaction on the single writer.</summary>
    internal Task<T> WriteAsync<T>(Func<WriteContext, T> work, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var op = new WriteOp<T>(work, ct);
        ObjectDisposedException.ThrowIf(!_queue.Writer.TryWrite(op), this);

        return op.Completion.Task;
    }

    /// <summary>Runs <paramref name="work"/> on a pooled read-only connection (a WAL snapshot).</summary>
    internal Task<T> ReadAsync<T>(Func<SqliteConnection, T> work, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return Task.Run(() =>
        {
            using var c = new SqliteConnection(_readConnectionString);
            c.Open();
            // One WAL snapshot for multi-statement reads. Raw BEGIN, so commands don't need a transaction object.
            Exec(c, null, "BEGIN DEFERRED");
            try
            {
                return work(c);
            }
            finally
            {
                Exec(c, null, "ROLLBACK");
            }
        }, ct);
    }

    private async Task WriterLoopAsync()
    {
        await foreach (var op in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            op.Run(this);
        }
    }

    private void RaiseEvents(WriteContext ctx)
    {
        foreach (var e in ctx.Events)
        {
            Invoke(NoteChanged, e);
        }

        if (ctx.LabelsChanged)
        {
            Invoke(LabelsChanged, EventArgs.Empty);
        }
    }

    /// <summary>A throwing subscriber must not kill the writer or hide the event from the others.</summary>
    private void Invoke<TArgs>(EventHandler<TArgs>? handler, TArgs args)
    {
        if (handler is null)
        {
            return;
        }

        foreach (var d in handler.GetInvocationList())
        {
            try
            {
                ((EventHandler<TArgs>)d)(this, args);
            }
#pragma warning disable CA1031 // subscriber faults are isolated by design (ADR-011)
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        }
    }

    private void Invoke(EventHandler? handler, EventArgs args)
    {
        if (handler is null)
        {
            return;
        }

        foreach (var d in handler.GetInvocationList())
        {
            try
            {
                ((EventHandler)d)(this, args);
            }
#pragma warning disable CA1031
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        }
    }
}
