#pragma warning disable xUnit1069 // Timeouts guard against a hang in the timer plumbing.
using FolderNotes.App.Library;
using FolderNotes.App.Services;
using FolderNotes.App.Settings;
using FolderNotes.App.Themes;
using FolderNotes.Core.Domain;
using FolderNotes.Core.Storage;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Win32;
using Serilog.Core;

namespace FolderNotes.App.Tests.Settings;

/// <summary>`HotkeyGesture` (03 § Settings, 05 § Settings page).</summary>
public sealed class HotkeyGestureTests
{
    [Theory]
    [InlineData("Ctrl+Alt+N", HotkeyGesture.ModControl | HotkeyGesture.ModAlt, 0x4E, "Ctrl+Alt+N", "Ctrl + Alt + N")]
    [InlineData("alt+shift+f12", HotkeyGesture.ModAlt | HotkeyGesture.ModShift, 0x7B, "Alt+Shift+F12", "Alt + Shift + F12")]
    [InlineData("Win + Space", HotkeyGesture.ModWin, 0x20, "Win+Space", "Win + Space")]
    [InlineData("Shift+Ctrl+7", HotkeyGesture.ModControl | HotkeyGesture.ModShift, 0x37, "Ctrl+Shift+7", "Ctrl + Shift + 7")]
    [InlineData("Ctrl+F24", HotkeyGesture.ModControl, 0x87, "Ctrl+F24", "Ctrl + F24")]
    public void REQ081_Parse_And_Format(string text, uint mods, uint vk, string stored, string display)
    {
        HotkeyGesture.TryParse(text, out var g).ShouldBeTrue();
        g.ShouldBe(new HotkeyGesture(mods, vk));
        g.ToString().ShouldBe(stored);
        g.DisplayText.ShouldBe(display);
        HotkeyGesture.TryParse(g.ToString(), out var again).ShouldBeTrue();
        again.ShouldBe(g);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("N")]
    [InlineData("Shift+N")] // Shift alone isn't enough
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+Ctrl+N")]
    [InlineData("Ctrl+Alt+Tab")]
    [InlineData("Ctrl+F25")]
    [InlineData("Hyper+N")]
    [InlineData("Ctrl+Alt")]
    public void REQ081_InvalidText_IsRejected(string? text) => HotkeyGesture.TryParse(text, out _).ShouldBeFalse();

    [Fact]
    public void REQ081_KeyPressRules()
    {
        HotkeyGesture.FromKeyPress(HotkeyGesture.ModControl | HotkeyGesture.ModShift, 0x4B).ShouldBe(new HotkeyGesture(HotkeyGesture.ModControl | HotkeyGesture.ModShift, 0x4B));
        HotkeyGesture.FromKeyPress(HotkeyGesture.ModShift, 0x4B).ShouldBeNull();
        HotkeyGesture.FromKeyPress(0, 0x4B).ShouldBeNull();
        HotkeyGesture.FromKeyPress(HotkeyGesture.ModControl, 0x11).ShouldBeNull(); // Ctrl itself
        HotkeyGesture.FromKeyPress(HotkeyGesture.ModAlt, 0x09).ShouldBeNull(); // Tab isn't allowed
        HotkeyGesture.Default.ToString().ShouldBe("Ctrl+Alt+N");
    }
}

/// <summary>`AppSettings` over the real store (02 § Settings, 03 § Settings).</summary>
public sealed class AppSettingsTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fn-settings-" + Guid.NewGuid().ToString("N")[..8]);
    private SqliteNoteStore _store = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _store = await SqliteNoteStore.OpenAsync(new SqliteNoteStoreOptions
        {
            DatabasePath = Path.Combine(_dir, "notes.db"),
            Identity = new Core.Identity.FolderIdentityService(),
        });
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task REQ081_Defaults_WhenNothingIsStored()
    {
        var s = new AppSettings(_store, Logger.None);
        await s.LoadAsync(Ct);

        s.Theme.ShouldBe(AppTheme.System);
        s.SearchHotkey.ShouldBe(HotkeyGesture.Default);
        s.Library.Matches(LibraryState.Default).ShouldBeTrue();
    }

    [Fact]
    public async Task REQ081_RoundTrip_ThroughTheStore()
    {
        var s = new AppSettings(_store, Logger.None);
        await s.LoadAsync(Ct);
        await s.SetThemeAsync(AppTheme.Dark);
        await s.SetSearchHotkeyAsync(new HotkeyGesture(HotkeyGesture.ModControl | HotkeyGesture.ModShift, 0x20));
        var state = new LibraryState(NoteSort.Path, LibraryViewMode.Cards, ["client", "Ünïcödé"], [NoteStatus.Keep, NoteStatus.Review]);
        await s.SetLibraryAsync(state);

        var all = await _store.GetAllSettingsAsync(Ct);
        all["theme"].ShouldBe("dark");
        all["search.hotkey"].ShouldBe("Ctrl+Shift+Space");
        all["library.sort"].ShouldBe("path");
        all["library.view"].ShouldBe("cards");
        all["library.statuses"].ShouldBe("[\"keep\",\"review\"]");

        var fresh = new AppSettings(_store, Logger.None);
        await fresh.LoadAsync(Ct);
        fresh.Theme.ShouldBe(AppTheme.Dark);
        fresh.SearchHotkey.ToString().ShouldBe("Ctrl+Shift+Space");
        fresh.Library.Matches(state).ShouldBeTrue();
    }

    [Fact]
    public async Task REQ081_InvalidStoredValues_GiveTheDefaults()
    {
        await _store.SetSettingAsync("theme", "purple", Ct);
        await _store.SetSettingAsync("search.hotkey", "Shift+N", Ct);
        await _store.SetSettingAsync("library.sort", "random", Ct);
        await _store.SetSettingAsync("library.view", "", Ct);
        await _store.SetSettingAsync("library.labels", "{not json", Ct);
        await _store.SetSettingAsync("library.statuses", "[\"keep\",\"bogus\"]", Ct);

        var s = new AppSettings(_store, Logger.None);
        await s.LoadAsync(Ct);

        s.Theme.ShouldBe(AppTheme.System);
        s.SearchHotkey.ShouldBe(HotkeyGesture.Default);
        s.Library.Matches(LibraryState.Default).ShouldBeTrue();
    }

    [Fact]
    public async Task REQ081_SetLibrary_WritesOnlyWhatChanged()
    {
        var s = new AppSettings(_store, Logger.None);
        await s.LoadAsync(Ct);
        await s.SetLibraryAsync(LibraryState.Default with { Sort = NoteSort.Title });

        (await _store.GetAllSettingsAsync(Ct)).Keys.ShouldBe(["library.sort"]);
    }
}

/// <summary>`RunKeyStartupRegistration` against a scratch HKCU key (never the real Run key).</summary>
public sealed class StartupRegistrationTests : IDisposable
{
    private readonly string _key = @"Software\FolderNotesTests\Run-" + Guid.NewGuid().ToString("N")[..8];

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(@"Software\FolderNotesTests", throwOnMissingSubKey: false);

    [Fact]
    public void REQ080_Enable_Disable_AndTheCommand()
    {
        var sut = new RunKeyStartupRegistration(@"C:\Apps\Folder Notes\FolderNotes.exe", _key);
        sut.IsEnabled.ShouldBeFalse();
        sut.Disable(); // nothing to remove: no error

        sut.Enable();
        sut.IsEnabled.ShouldBeTrue();
        using (var k = Registry.CurrentUser.OpenSubKey(_key))
        {
            k!.GetValue("FolderNotes").ShouldBe("\"C:\\Apps\\Folder Notes\\FolderNotes.exe\" --background");
        }

        sut.Disable();
        sut.IsEnabled.ShouldBeFalse();
    }

    [Fact]
    public void ADR022_RepairPath_OnlyTouchesAnExistingEntryThatPointsElsewhere()
    {
        var old = new RunKeyStartupRegistration(@"C:\Old\FolderNotes.exe", _key);
        var now = new RunKeyStartupRegistration(@"C:\New\FolderNotes.exe", _key);

        now.RepairPath().ShouldBeFalse(); // no entry: never created by the app itself
        now.IsEnabled.ShouldBeFalse();

        old.Enable();
        now.IsEnabled.ShouldBeTrue(); // another copy counts as on
        now.RepairPath().ShouldBeTrue();
        now.RepairPath().ShouldBeFalse();
        using var k = Registry.CurrentUser.OpenSubKey(_key);
        k!.GetValue("FolderNotes").ShouldBe("\"C:\\New\\FolderNotes.exe\" --background");
    }
}

/// <summary>`SettingsViewModel` (05 § Settings page), with fakes for the hotkey host and the Run entry.</summary>
public sealed class SettingsViewModelTests : IAsyncLifetime
{
    private static readonly HotkeyGesture CtrlAltK = new(HotkeyGesture.ModControl | HotkeyGesture.ModAlt, 0x4B);
    private static readonly HotkeyGesture CtrlShiftF = new(HotkeyGesture.ModControl | HotkeyGesture.ModShift, 0x46);
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero));
    private readonly FakeSettingsStore _store = new();
    private readonly FakeHotkeys _hotkeys = new();
    private readonly FakeStartup _startup = new();
    private readonly List<AppTheme> _applied = [];
    private AppSettings _settings = null!;

    public async ValueTask InitializeAsync()
    {
        _settings = new AppSettings(_store, Logger.None);
        await _settings.LoadAsync();
        _hotkeys.TryChange(_settings.SearchHotkey);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private SettingsViewModel Create(Func<string, bool>? open = null) =>
        new(_settings, _applied.Add, _hotkeys, _startup, _clock, new InlineDispatcher(), Logger.None, "0.1.0", @"C:\Data\FolderNotes", open);

    [Fact]
    public async Task REQ081_Theme_AppliesAndSaves_AFailedSaveGoesBack()
    {
        using var vm = Create();
        vm.Theme.ShouldBe(AppTheme.System);

        await vm.SetThemeAsync(AppTheme.Dark);
        _applied.ShouldBe([AppTheme.Dark]);
        _store.Values["theme"].ShouldBe("dark");

        _store.FailWrites = true;
        await vm.SetThemeAsync(AppTheme.Light);
        vm.Theme.ShouldBe(AppTheme.Dark);
        _applied.ShouldBe([AppTheme.Dark, AppTheme.Light, AppTheme.Dark]);
        vm.ThemeError.ShouldBe("Couldn't save the theme: disk full");
    }

    [Fact]
    public async Task REQ050_Capture_NewShortcut_RegistersThenSaves_AndShowsSaved()
    {
        using var vm = Create();
        vm.HotkeyText.ShouldBe("Ctrl + Alt + N");
        vm.HotkeyAccessibleName.ShouldBe("Search shortcut, Ctrl + Alt + N");
        vm.HotkeyWarning.ShouldBeNull();

        vm.BeginCapture();
        vm.IsCapturing.ShouldBeTrue();
        vm.HotkeyMessage.ShouldBe("Press the new shortcut… Esc to cancel.");
        (await vm.OnKeyPressAsync(HotkeyGesture.ModControl, 0x11)).ShouldBeFalse(); // Ctrl alone: keep waiting, no complaint
        vm.HotkeyMessage.ShouldBe("Press the new shortcut… Esc to cancel.");
        (await vm.OnKeyPressAsync(HotkeyGesture.ModShift, 0x4B)).ShouldBeFalse(); // Shift+K: not allowed
        vm.HotkeyMessage.ShouldBe("Use Ctrl, Alt or Win with a letter, digit, F1–F24 or Space.");
        vm.IsCapturing.ShouldBeTrue();

        (await vm.OnKeyPressAsync(CtrlAltK.Modifiers, CtrlAltK.VirtualKey)).ShouldBeTrue();

        vm.IsCapturing.ShouldBeFalse();
        _hotkeys.Registered.ShouldBe(CtrlAltK);
        _store.Values["search.hotkey"].ShouldBe("Ctrl+Alt+K");
        vm.HotkeyText.ShouldBe("Ctrl + Alt + K");
        vm.HotkeyMessage.ShouldBe("Saved");
        _clock.Advance(SettingsViewModel.SavedMessageTime);
        vm.HotkeyMessage.ShouldBeNull();
    }

    [Fact]
    public async Task REQ050_Capture_EscCancels()
    {
        using var vm = Create();
        vm.BeginCapture();
        (await vm.OnKeyPressAsync(0, 0x1B)).ShouldBeTrue();
        vm.IsCapturing.ShouldBeFalse();
        vm.HotkeyMessage.ShouldBeNull();
        _hotkeys.Registered.ShouldBe(HotkeyGesture.Default);
    }

    [Fact]
    public async Task REQ050_AShortcutAnotherAppOwns_IsRefused_AndTheCurrentOneStays()
    {
        _hotkeys.Taken.Add(CtrlAltK);
        using var vm = Create();
        vm.BeginCapture();

        await vm.OnKeyPressAsync(CtrlAltK.Modifiers, CtrlAltK.VirtualKey);

        vm.HotkeyMessage.ShouldBe("Ctrl + Alt + K is used by another app. Choose another.");
        vm.HotkeyMessageIsError.ShouldBeTrue();
        _hotkeys.Registered.ShouldBe(HotkeyGesture.Default);
        _store.Values.ContainsKey("search.hotkey").ShouldBeFalse();
        vm.HotkeyText.ShouldBe("Ctrl + Alt + N");
        vm.HotkeyWarning.ShouldBeNull();
    }

    [Fact]
    public async Task REQ050_ASaveFailure_PutsThePreviousShortcutBack()
    {
        using var vm = Create();
        _store.FailWrites = true;
        vm.BeginCapture();

        await vm.OnKeyPressAsync(CtrlShiftF.Modifiers, CtrlShiftF.VirtualKey);

        _hotkeys.Registered.ShouldBe(HotkeyGesture.Default);
        vm.HotkeyMessage.ShouldBe("Couldn't save the shortcut: disk full");
        vm.HotkeyText.ShouldBe("Ctrl + Alt + N");
    }

    [Fact]
    public async Task REQ050_StartupFailure_ShowsAWarning_UntilAShortcutRegisters_ThenResetWorks()
    {
        _hotkeys.Taken.Add(HotkeyGesture.Default);
        _hotkeys.Clear(); // what startup saw: Ctrl+Alt+N refused
        using var vm = Create();

        vm.HotkeyWarning.ShouldBe("Ctrl + Alt + N couldn't be registered: another app uses it. Choose another, or close that app and restart Folder Notes.");

        vm.BeginCapture();
        await vm.OnKeyPressAsync(CtrlShiftF.Modifiers, CtrlShiftF.VirtualKey);
        vm.HotkeyWarning.ShouldBeNull();
        _hotkeys.Registered.ShouldBe(CtrlShiftF);

        _hotkeys.Taken.Clear();
        await vm.ResetHotkeyAsync();
        _hotkeys.Registered.ShouldBe(HotkeyGesture.Default);
        _store.Values["search.hotkey"].ShouldBe("Ctrl+Alt+N");
        vm.HotkeyText.ShouldBe("Ctrl + Alt + N");
    }

    [Fact]
    public void REQ080_StartWithWindows_ShowsTheRealEntry_AndToggles_FailuresGoBack()
    {
        _startup.Enabled = true; // e.g. the installer turned it on
        using var vm = Create();
        vm.StartWithWindows.ShouldBeTrue();

        vm.SetStartWithWindows(false);
        _startup.Enabled.ShouldBeFalse();
        vm.StartWithWindows.ShouldBeFalse();

        _startup.Fail = true;
        vm.SetStartWithWindows(true);
        vm.StartWithWindows.ShouldBeFalse();
        vm.StartupError.ShouldBe("Couldn't change this: access denied");

        _startup.Fail = false;
        _startup.Enabled = true; // turned on elsewhere (Task Manager)
        vm.Refresh();
        vm.StartWithWindows.ShouldBeTrue();
    }

    [Fact]
    public void REQ012_ManageLabels_AndAbout()
    {
        string? opened = null;
        using var vm = Create(p => { opened = p; return true; });
        var asked = false;
        vm.ManageLabelsRequested += (_, _) => asked = true;

        vm.ManageLabels();
        vm.OpenDataFolder().ShouldBeTrue();

        asked.ShouldBeTrue();
        opened.ShouldBe(@"C:\Data\FolderNotes");
        vm.Version.ShouldBe("0.1.0");
    }

    private sealed class FakeSettingsStore : ISettingsStore
    {
        public Dictionary<string, string> Values { get; } = [];
        public bool FailWrites { get; set; }

        public Task<IReadOnlyDictionary<string, string>> GetAllSettingsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>(Values));

        public Task SetSettingAsync(string key, string value, CancellationToken ct = default)
        {
            if (FailWrites)
            {
                throw new IOException("disk full");
            }

            Values[key] = value;
            return Task.CompletedTask;
        }

        public Task RemoveSettingAsync(string key, CancellationToken ct = default)
        {
            Values.Remove(key);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeHotkeys : IHotkeyHost
    {
        public HashSet<HotkeyGesture> Taken { get; } = [];
        public HotkeyGesture? Registered { get; private set; }

        public void Clear() => Registered = null;

        public bool TryChange(HotkeyGesture gesture)
        {
            if (Taken.Contains(gesture))
            {
                return false; // the previous one stays registered
            }

            Registered = gesture;
            return true;
        }
    }

    private sealed class FakeStartup : IStartupRegistration
    {
        public bool Enabled { get; set; }
        public bool Fail { get; set; }
        public bool IsEnabled => Enabled;

        public void Enable() => Enabled = !Fail ? true : throw new UnauthorizedAccessException("access denied");

        public void Disable() => Enabled = !Fail ? false : throw new UnauthorizedAccessException("access denied");

        public bool RepairPath() => false;
    }

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public bool Post(Action action)
        {
            action();
            return true;
        }
    }
}
