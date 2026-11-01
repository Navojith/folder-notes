namespace FolderNotes.Core.Storage;

/// <summary>App settings as key/value strings (03 § Settings, 02 § Settings, ADR-022). The app owns keys and formats.</summary>
public interface ISettingsStore
{
    /// <summary>Every stored setting (keys are ordinal).</summary>
    Task<IReadOnlyDictionary<string, string>> GetAllSettingsAsync(CancellationToken ct = default);

    /// <summary>Inserts or replaces one setting, through the single writer (ADR-011).</summary>
    Task SetSettingAsync(string key, string value, CancellationToken ct = default);

    /// <summary>Removes a setting (back to the default). No error when it isn't there.</summary>
    Task RemoveSettingAsync(string key, CancellationToken ct = default);
}

public sealed partial class SqliteNoteStore : ISettingsStore
{
    public Task<IReadOnlyDictionary<string, string>> GetAllSettingsAsync(CancellationToken ct = default) =>
        ReadAsync<IReadOnlyDictionary<string, string>>(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT key, value FROM settings";
            using var r = cmd.ExecuteReader();
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            while (r.Read())
            {
                result[r.GetString(0)] = r.GetString(1);
            }

            return result;
        }, ct);

    public Task SetSettingAsync(string key, string value, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        return WriteAsync(ctx =>
        {
            using var cmd = ctx.Command("INSERT INTO settings (key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value");
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$v", value);
            return cmd.ExecuteNonQuery();
        }, ct);
    }

    public Task RemoveSettingAsync(string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return WriteAsync(ctx =>
        {
            using var cmd = ctx.Command("DELETE FROM settings WHERE key = $k");
            cmd.Parameters.AddWithValue("$k", key);
            return cmd.ExecuteNonQuery();
        }, ct);
    }
}
