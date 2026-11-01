using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using FolderNotes.App.Services;
using FolderNotes.Core.Domain;
using FolderNotes.Core.Storage;
using Serilog;

namespace FolderNotes.App.Labels;

/// <summary>One label on the Labels page (specs/05 § Labels page).</summary>
public sealed partial class LabelItem : ObservableObject
{
    internal LabelItem(Label label, int usage)
    {
        Label = label;
        Usage = usage;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name), nameof(Color), nameof(AccessibleName))]
    public partial Label Label { get; internal set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsageText), nameof(IsUsed), nameof(AccessibleName))]
    public partial int Usage { get; internal set; }

    /// <summary>True while the name is a text box (Rename).</summary>
    [ObservableProperty]
    public partial bool IsEditing { get; internal set; }

    /// <summary>The rename box's text.</summary>
    [ObservableProperty]
    public partial string EditName { get; set; } = "";

    /// <summary>The last rename / recolour error for this row, or null.</summary>
    [ObservableProperty]
    public partial string? Error { get; internal set; }

    public Guid Id => Label.Id;

    public string Name => Label.Name;

    public string Color => Label.Color;

    public bool IsUsed => Usage > 0;

    public string UsageText => Usage switch
    {
        0 => "Not used",
        1 => "Used by 1 note",
        _ => "Used by " + Usage.ToString(CultureInfo.CurrentCulture) + " notes",
    };

    public string AccessibleName => $"Label {Name}, {UsageText.ToLowerInvariant()}";

    public override string ToString() => AccessibleName;
}

/// <summary>
/// The Labels page (specs/05 § Labels page, 02 § Labels page, REQ-012/013): list with usage, create,
/// rename, recolour, delete. UI-thread only; no XAML types.
/// </summary>
public sealed partial class LabelsViewModel : ObservableObject, IDisposable
{
    private readonly ILabelRepository _labels;
    private readonly INoteEvents _events;
    private readonly ILogger _log;
    private Task _reload = Task.CompletedTask;
    private bool _reloadAgain;
    private bool _loadedOnce;
    private bool _disposed;

    internal LabelsViewModel(ILabelRepository labels, INoteEvents events, ILogger log)
    {
        _labels = labels ?? throw new ArgumentNullException(nameof(labels));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _log = (log ?? throw new ArgumentNullException(nameof(log))).ForContext<LabelsViewModel>();
        _events.LabelsChanged += OnChanged;
        _events.NoteChanged += OnChanged;
    }

    public ObservableCollection<LabelItem> Items { get; } = [];

    [ObservableProperty]
    public partial string CountText { get; private set; } = "";

    // ───────────── new label form ─────────────

    [ObservableProperty]
    public partial string NewName { get; set; } = "";

    /// <summary>Preset to the least-used palette colour (05).</summary>
    [ObservableProperty]
    public partial string NewColor { get; set; } = Palette.Keys[0];

    [ObservableProperty]
    public partial string? NewError { get; private set; }

    /// <summary>Typing clears a stale error (05).</summary>
    partial void OnNewNameChanged(string value) => NewError = null;

    // ───────────── loading ─────────────

    public Task ActivateAsync() => _loadedOnce ? Task.CompletedTask : ReloadAsync();

    /// <summary>Coalesced like the library's (02 § Labels page).</summary>
    public Task ReloadAsync()
    {
        if (!_reload.IsCompleted)
        {
            _reloadAgain = true;
            return _reload;
        }

        _reload = RunReloadsAsync();
        return _reload;
    }

    internal Task WhenIdleAsync() => _reload;

    private async Task RunReloadsAsync()
    {
        do
        {
            _reloadAgain = false;
            await ReloadOnceAsync();
        }
        while (_reloadAgain && !_disposed);
    }

    private async Task ReloadOnceAsync()
    {
        var labels = await _labels.ListLabelsAsync();
        var usage = await _labels.CountNotesByLabelAsync();

        // Update rows in place so a row being renamed keeps its edit state.
        var byId = Items.ToDictionary(i => i.Id);
        var next = new List<LabelItem>(labels.Count);
        foreach (var label in labels)
        {
            var count = usage.GetValueOrDefault(label.Id);
            if (byId.TryGetValue(label.Id, out var item))
            {
                item.Label = label;
                item.Usage = count;
            }
            else
            {
                item = new LabelItem(label, count);
            }

            next.Add(item);
        }

        if (!Items.SequenceEqual(next))
        {
            Items.Clear();
            foreach (var item in next)
            {
                Items.Add(item);
            }
        }

        CountText = labels.Count == 1 ? "1 label" : labels.Count.ToString(CultureInfo.CurrentCulture) + " labels";
        if (!_loadedOnce || NewName.Length == 0)
        {
            NewColor = Palette.LeastUsed(labels.Select(l => l.Color));
        }

        _loadedOnce = true;
    }

    // ───────────── create ─────────────

    /// <summary>[Add] / Enter in the new-label box. True when the label was created.</summary>
    public async Task<bool> CreateAsync()
    {
        var name = NewName.Trim();
        var error = NameError(new Label(Guid.Empty, name, NewColor), exceptId: null);
        if (error is not null)
        {
            NewError = error;
            return false;
        }

        try
        {
            await _labels.CreateLabelAsync(name, NewColor);
        }
        catch (Exception ex) when (ex is DuplicateLabelException or NoteValidationException)
        {
            NewError = Message(ex, name);
            return false;
        }

        _log.Information("Created label {Name} ({Color})", name, NewColor);
        NewError = null;
        NewName = "";
        await ReloadAsync(); // also presets the next least-used colour
        return true;
    }

    // ───────────── rename / recolour / delete ─────────────

    public void BeginRename(LabelItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        foreach (var other in Items.Where(i => i.IsEditing && !ReferenceEquals(i, item)))
        {
            CancelRename(other);
        }

        item.EditName = item.Name;
        item.Error = null;
        item.IsEditing = true;
    }

    public static void CancelRename(LabelItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        item.IsEditing = false;
        item.Error = null;
        item.EditName = item.Name;
    }

    /// <summary>Enter / leaving the box. On an error the row stays in edit mode (05).</summary>
    public async Task<bool> CommitRenameAsync(LabelItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!item.IsEditing)
        {
            return true;
        }

        var name = item.EditName.Trim();
        if (string.Equals(name, item.Name, StringComparison.Ordinal))
        {
            CancelRename(item);
            return true;
        }

        var renamed = item.Label with { Name = name };
        var error = NameError(renamed, exceptId: item.Id);
        if (error is not null)
        {
            item.Error = error;
            return false;
        }

        if (!await UpdateAsync(item, renamed))
        {
            return false;
        }

        _log.Information("Renamed label {Old} to {New}", item.Name, name);
        item.IsEditing = false;
        item.Label = renamed;
        await ReloadAsync();
        return true;
    }

    public async Task<bool> RecolorAsync(LabelItem item, string color)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (string.Equals(color, item.Color, StringComparison.Ordinal))
        {
            return true;
        }

        var recolored = item.Label with { Color = color };
        if (!await UpdateAsync(item, recolored))
        {
            return false;
        }

        item.Label = recolored;
        await ReloadAsync();
        return true;
    }

    /// <summary>After the view confirmed (05: no undo, ADR-018).</summary>
    public async Task DeleteAsync(LabelItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        try
        {
            await _labels.DeleteLabelAsync(item.Id);
        }
        catch (KeyNotFoundException)
        {
            // Already gone; the reload below shows that.
        }

        _log.Information("Deleted label {Name} (was on {Count} notes)", item.Name, item.Usage);
        await ReloadAsync();
    }

    /// <summary>The confirmation text for <see cref="DeleteAsync"/>.</summary>
    public static (string Title, string? Message) DeleteConfirmation(LabelItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var message = item.Usage switch
        {
            0 => null,
            1 => "It will be removed from 1 note.",
            _ => $"It will be removed from {item.Usage.ToString(CultureInfo.CurrentCulture)} notes.",
        };
        return ($"Delete label '{item.Name}'?", message);
    }

    private async Task<bool> UpdateAsync(LabelItem item, Label updated)
    {
        try
        {
            await _labels.UpdateLabelAsync(updated);
            item.Error = null;
            return true;
        }
        catch (Exception ex) when (ex is DuplicateLabelException or NoteValidationException)
        {
            item.Error = Message(ex, updated.Name);
            return false;
        }
        catch (KeyNotFoundException)
        {
            item.Error = "This label no longer exists.";
            await ReloadAsync();
            return false;
        }
    }

    /// <summary>The first rule error, or the duplicate message; null when the name is fine.</summary>
    private string? NameError(Label label, Guid? exceptId)
    {
        var rules = NoteValidator.ValidateLabel(label);
        if (rules.Count > 0)
        {
            return rules[0].Message;
        }

        return Items.Any(i => i.Id != exceptId && Label.NameComparer.Equals(i.Name, label.Name))
            ? DuplicateMessage(label.Name)
            : null;
    }

    private static string Message(Exception ex, string name) => ex switch
    {
        DuplicateLabelException => DuplicateMessage(name),
        NoteValidationException v when v.Errors.Count > 0 => v.Errors[0].Message,
        _ => ex.Message,
    };

    private static string DuplicateMessage(string name) => $"A label named '{name}' already exists.";

    // ───────────── sync ─────────────

    private async void OnChanged(object? sender, EventArgs e)
    {
        try
        {
            if (_loadedOnce && !_disposed)
            {
                await ReloadAsync();
            }
        }
#pragma warning disable CA1031 // An event handler must not crash the UI thread; the next reload repairs the page.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log.Error(ex, "Reloading the Labels page failed");
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _events.LabelsChanged -= OnChanged;
        _events.NoteChanged -= OnChanged;
    }
}
