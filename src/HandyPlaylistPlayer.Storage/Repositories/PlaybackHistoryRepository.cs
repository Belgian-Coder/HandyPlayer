using System.Globalization;
using HandyPlaylistPlayer.Core.Interfaces;
using HandyPlaylistPlayer.Core.Models;
using Microsoft.Data.Sqlite;

namespace HandyPlaylistPlayer.Storage.Repositories;

public class PlaybackHistoryRepository(DatabaseConfig config) : IPlaybackHistoryRepository
{
    public async Task<int> RecordAsync(int mediaFileId, long durationMs)
    {
        await using var conn = new SqliteConnection(config.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO playback_history (media_file_id, duration_ms)
            VALUES (@mid, @dur)
            RETURNING id
            """;
        cmd.Parameters.AddWithValue("@mid", mediaFileId);
        cmd.Parameters.AddWithValue("@dur", durationMs);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    public async Task<List<PlaybackHistoryEntry>> GetRecentAsync(int limit = 50)
    {
        await using var conn = new SqliteConnection(config.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT ph.id, ph.media_file_id, mf.filename, ph.started_at, ph.duration_ms
            FROM playback_history ph
            JOIN media_files mf ON mf.id = ph.media_file_id
            ORDER BY ph.started_at DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@limit", limit);

        await using var reader = await cmd.ExecuteReaderAsync();
        var entries = new List<PlaybackHistoryEntry>();
        while (await reader.ReadAsync())
        {
            entries.Add(new PlaybackHistoryEntry
            {
                Id = reader.GetInt32(0),
                MediaFileId = reader.GetInt32(1),
                Filename = reader.GetString(2),
                StartedAt = DateTime.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
                DurationMs = reader.GetInt64(4)
            });
        }
        return entries;
    }

    public async Task<int> GetTotalPlayCountAsync()
    {
        await using var conn = new SqliteConnection(config.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM playback_history";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    public async Task<long> GetTotalWatchTimeAsync()
    {
        await using var conn = new SqliteConnection(config.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(SUM(duration_ms), 0) FROM playback_history";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }
}
