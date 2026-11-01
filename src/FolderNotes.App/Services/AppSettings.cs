using System.Text.Json;
using FolderNotes.App.Library;
using FolderNotes.App.Themes;
using FolderNotes.Core.Domain;
using FolderNotes.Core.Storage;
using Serilog;

namespace FolderNotes.App.Services;

/// <summary>
/// Typed settings over <see cref="ISettingsStore"/> (02 § Settings, 03 § Settings, ADR-022). Bad
/// stored values are logged and read as the default, never a startup failure. Setters write the
/// store first and update the in-memory copy only when that worked.
/// </summary>
internal sealed class AppSettings
{
    internal const string ThemeKey = "theme";
    internal const string HotkeyKey = "search.hotkey";
    internal const string SortKey = "library.sort";
    internal const string ViewKey = "library.view";
    internal const string LabelsKey = "library.labels";
    internal const string StatusesKey = "library.statuses";

    private readonly ISettingsStore _store;
    private readonly ILogger _log;

    public AppSettings(ISettingsStore store, ILogger log)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _log = (log ?? throw new ArgumentNullException(nameof(log))).ForContext<AppSettings>();
    }

    public AppTheme Theme { get; private set; } = AppTheme.System;

    public HotkeyGesture SearchHotkey { get; private set; } = HotkeyGesture.Default;

    public LibraryState Library { get; private set; } = LibraryState.Default;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        var all = await _store.GetAllSettingsAsync(ct).ConfigureAwait(false);
        Theme = ReadValue(all, ThemeKey, AppTheme.System, ParseTheme);
        SearchHotkey = Read(all, HotkeyKey, HotkeyGesture.Default, s => HotkeyGesture.TryParse(s, out var g) ? g : null);
        Library = new LibraryState(
            ReadValue(all, SortKey, NoteSort.UpdatedDesc, ParseSort),
            ReadValue(all, ViewKey, LibraryViewMode.List, ParseView),
            Read<IReadOnlyList<string>>(all, LabelsKey, [], s => ParseList(s)?.Where(n => n.Trim().Length > 0).Select(n => n.Trim()).ToArray()),
            Read<IReadOnlyList<NoteStatus>>(all, StatusesKey, [], ParseStatuses));
    }

    public async Task SetThemeAsync(AppTheme theme)
    {
        await _store.SetSettingAsync(ThemeKey, ThemeText(theme)).ConfigureAwait(false);
        Theme = theme;
    }

    public async Task SetSearchHotkeyAsync(HotkeyGesture gesture)
    {
        ArgumentNullException.ThrowIfNull(gesture);
        await _store.SetSettingAsync(HotkeyKey, gesture.ToString()).ConfigureAwait(false);
        SearchHotkey = gesture;
    }

    /// <summary>Writes only the keys that changed.</summary>
    public async Task SetLibraryAsync(LibraryState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var old = Library;
        if (state.Sort != old.Sort)
        {
            await _store.SetSettingAsync(SortKey, SortText(state.Sort)).ConfigureAwait(false);
        }

        if (state.View != old.View)
        {
            await _store.SetSettingAsync(ViewKey, state.View == LibraryViewMode.Cards ? "cards" : "list").ConfigureAwait(false);
        }

        if (!state.Labels.SequenceEqual(old.Labels, StringComparer.Ordinal))
        {
            await _store.SetSettingAsync(LabelsKey, JsonSerializer.Serialize(state.Labels)).ConfigureAwait(false);
        }

        if (!state.Statuses.SequenceEqual(old.Statuses))
        {
            await _store.SetSettingAsync(StatusesKey, JsonSerializer.Serialize(state.Statuses.Select(s => s.ToKey()))).ConfigureAwait(false);
        }

        Library = state;
    }

    private T ReadValue<T>(IReadOnlyDictionary<string, string> all, string key, T fallback, Func<string, T?> parse)
        where T : struct =>
        Read<object>(all, key, fallback, raw => parse(raw) is { } v ? v : null) is T t ? t : fallback;

    private T Read<T>(IReadOnlyDictionary<string, string> all, string key, T fallback, Func<string, T?> parse)
        where T : class
    {
        if (!all.TryGetValue(key, out var raw))
        {
            return fallback;
        }

        T? value;
        try
        {
            value = parse(raw);
        }
        catch (JsonException)
        {
            value = default;
        }

        if (value is null)
        {
            _log.Warning("Setting {Key} has an invalid value {Value}; using the default", key, raw);
            return fallback;
        }

        return value;
    }

    internal static string ThemeText(AppTheme theme) => theme switch
    {
        AppTheme.Light => "light",
        AppTheme.Dark => "dark",
        _ => "system",
    };

    private static AppTheme? ParseTheme(string s) => s switch
    {
        "system" => AppTheme.System,
        "light" => AppTheme.Light,
        "dark" => AppTheme.Dark,
        _ => null,
    };

    internal static string SortText(NoteSort sort) => sort switch
    {
        NoteSort.Title => "title",
        NoteSort.DueDate => "due",
        NoteSort.Path => "path",
        _ => "updated",
    };

    private static NoteSort? ParseSort(string s) => s switch
    {
        "updated" => NoteSort.UpdatedDesc,
        "title" => NoteSort.Title,
        "due" => NoteSort.DueDate,
        "path" => NoteSort.Path,
        _ => null,
    };

    private static LibraryViewMode? ParseView(string s) => s switch
    {
        "list" => LibraryViewMode.List,
        "cards" => LibraryViewMode.Cards,
        _ => null,
    };

    private static string[]? ParseList(string s) => JsonSerializer.Deserialize<string[]>(s);

    private static NoteStatus[]? ParseStatuses(string s)
    {
        var keys = ParseList(s);
        if (keys is null)
        {
            return null;
        }

        var result = new List<NoteStatus>();
        foreach (var k in keys)
        {
            if (!NoteStatusExtensions.TryParseKey(k, out var status) || status == NoteStatus.None)
            {
                return null;
            }

            if (!result.Contains(status))
            {
                result.Add(status);
            }
        }

        return [.. result];
    }
}
