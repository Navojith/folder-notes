namespace FolderNotes.Core.Tests.Storage;

/// <summary>03 § Settings, 02 § Settings (T2.8): the key/value store.</summary>
public sealed class SettingsStoreTests : IAsyncLifetime
{
    private StoreFixture _f = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _f = await StoreFixture.CreateAsync();

    public ValueTask DisposeAsync() => _f.DisposeAsync();

    [Fact]
    public async Task REQ081_NewDatabase_HasNoSettings()
    {
        (await _f.Store.GetAllSettingsAsync(Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task REQ081_Set_Overwrite_Remove_RoundTrip()
    {
        await _f.Store.SetSettingAsync("theme", "dark", Ct);
        await _f.Store.SetSettingAsync("search.hotkey", "Ctrl+Alt+N", Ct);
        await _f.Store.SetSettingAsync("theme", "light", Ct);

        var all = await _f.Store.GetAllSettingsAsync(Ct);
        all.Count.ShouldBe(2);
        all["theme"].ShouldBe("light");
        all["search.hotkey"].ShouldBe("Ctrl+Alt+N");

        await _f.Store.RemoveSettingAsync("theme", Ct);
        await _f.Store.RemoveSettingAsync("not-there", Ct);
        (await _f.Store.GetAllSettingsAsync(Ct)).Keys.ShouldBe(["search.hotkey"]);
    }

    [Fact]
    public async Task REQ081_Settings_SurviveAReopen_AndKeepUnicode()
    {
        await _f.Store.SetSettingAsync("library.labels", "[\"client\",\"Ünïcödé ✓\"]", Ct);
        await _f.ReopenAsync();

        (await _f.Store.GetAllSettingsAsync(Ct))["library.labels"].ShouldBe("[\"client\",\"Ünïcödé ✓\"]");
    }

    [Fact]
    public async Task REQ081_BadKey_IsRejected()
    {
        await Should.ThrowAsync<ArgumentException>(() => _f.Store.SetSettingAsync(" ", "x", Ct));
    }
}
