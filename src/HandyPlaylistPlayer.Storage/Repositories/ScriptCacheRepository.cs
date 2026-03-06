using HandyPlaylistPlayer.Core.Interfaces;
using Microsoft.Data.Sqlite;

namespace HandyPlaylistPlayer.Storage.Repositories;

public class ScriptCacheRepository(DatabaseConfig config) : IScriptCacheService
{
    public async Task<string?> GetUrlBySha256Async(string sha256)
    {
        await using var conn = new SqliteConnection(config.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT hosted_url FROM script_cache WHERE sha256 = @sha256";
        cmd.Parameters.AddWithValue("@sha256", sha256);
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull or null ? null : (string)result;
    }

    public async Task UpsertAsync(string sha256, string hostedUrl)
    {
        await using var conn = new SqliteConnection(config.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO script_cache (sha256, hosted_url) VALUES (@sha256, @url)
            ON CONFLICT(sha256) DO UPDATE SET hosted_url = excluded.hosted_url, uploaded_at = datetime('now')
            """;
        cmd.Parameters.AddWithValue("@sha256", sha256);
        cmd.Parameters.AddWithValue("@url", hostedUrl);
        await cmd.ExecuteNonQueryAsync();
    }
}
