using HandyPlaylistPlayer.Core.Interfaces;
using Microsoft.Data.Sqlite;

namespace HandyPlaylistPlayer.Storage.Repositories;

public class AppSettingsRepository(DatabaseConfig config) : IAppSettingsRepository
{
    public async Task<string?> GetAsync(string key)
    {
        await using var conn = new SqliteConnection(config.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM app_settings WHERE key = @key";
        cmd.Parameters.AddWithValue("@key", key);
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull or null ? null : (string)result;
    }

    public async Task SetAsync(string key, string value)
    {
        await using var conn = new SqliteConnection(config.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO app_settings (key, value) VALUES (@key, @value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value
            """;
        cmd.Parameters.AddWithValue("@key", key);
        cmd.Parameters.AddWithValue("@value", value);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DeleteAsync(string key)
    {
        await using var conn = new SqliteConnection(config.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM app_settings WHERE key = @key";
        cmd.Parameters.AddWithValue("@key", key);
        await cmd.ExecuteNonQueryAsync();
    }
}
