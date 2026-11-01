using FolderNotes.Core.Storage;
using Microsoft.UI.Dispatching;
using Serilog;

namespace FolderNotes.App.Services;

/// <summary>Posts work to the UI thread. Abstracted so the relay can be tested without XAML.</summary>
internal interface IUiDispatcher
{
    /// <summary>Queues <paramref name="action"/> on the UI thread. False when the queue is shutting down.</summary>
    bool Post(Action action);
}

internal sealed class DispatcherQueueUiDispatcher(DispatcherQueue queue) : IUiDispatcher
{
    private readonly DispatcherQueue _queue = queue ?? throw new ArgumentNullException(nameof(queue));

    public bool Post(Action action) => _queue.TryEnqueue(() => action());
}

/// <summary>Note and label change events, raised on the UI thread (02 § App shell, § Library).</summary>
internal interface INoteEvents
{
    event EventHandler<NoteChangedEventArgs>? NoteChanged;

    event EventHandler? LabelsChanged;
}

/// <summary>
/// Re-raises the store's events on the UI thread (O-11, ADR-011). UI code subscribes here, never to
/// the store, so handlers can touch XAML and never delay the writer task.
/// </summary>
internal sealed class UiNoteEvents : INoteEvents, IDisposable
{
    private readonly INoteRepository _notes;
    private readonly ILabelRepository _labels;
    private readonly IUiDispatcher _ui;
    private readonly ILogger _log;

    public UiNoteEvents(INoteRepository notes, ILabelRepository labels, IUiDispatcher ui, ILogger log)
    {
        _notes = notes ?? throw new ArgumentNullException(nameof(notes));
        _labels = labels ?? throw new ArgumentNullException(nameof(labels));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _log = (log ?? throw new ArgumentNullException(nameof(log))).ForContext<UiNoteEvents>();
        _notes.NoteChanged += OnNoteChanged;
        _labels.LabelsChanged += OnLabelsChanged;
    }

    /// <summary>Raised on the UI thread after a note write commits.</summary>
    public event EventHandler<NoteChangedEventArgs>? NoteChanged;

    /// <summary>Raised on the UI thread after a label write commits.</summary>
    public event EventHandler? LabelsChanged;

    public void Dispose()
    {
        _notes.NoteChanged -= OnNoteChanged;
        _labels.LabelsChanged -= OnLabelsChanged;
    }

    private void OnNoteChanged(object? sender, NoteChangedEventArgs e) => Post(() => NoteChanged?.Invoke(this, e));

    private void OnLabelsChanged(object? sender, EventArgs e) => Post(() => LabelsChanged?.Invoke(this, e));

    private void Post(Action raise)
    {
        if (!_ui.Post(() => Invoke(raise)))
        {
            _log.Debug("UI queue closed; dropped a change notification");
        }
    }

    private void Invoke(Action raise)
    {
        try
        {
            raise();
        }
#pragma warning disable CA1031 // One faulty UI handler must not take down the dispatcher loop.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log.Error(ex, "A NoteChanged/LabelsChanged UI handler threw");
        }
    }
}
