using HandyPlaylistPlayer.Core.Models;
using Microsoft.Data.Sqlite;

namespace HandyPlaylistPlayer.Storage.Repositories;

public class PairingRepository(DatabaseConfig config) : HandyPlaylistPlayer.Core.Interfaces.IPairingRepository
{
    public async Task<Pairing?> GetForVideoAsync(int videoFileId)
    {
        await using var conn = new SqliteConnection(config.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, video_file_id, script_file_id, is_manual, confidence, offset_ms
            FROM pairings WHERE video_file_id = @vid
            ORDER BY is_manual DESC, confidence DESC
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@vid", videoFileId);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return new Pairing
        {
            Id = reader.GetInt32(0),
            VideoFileId = reader.GetInt32(1),
            ScriptFileId = reader.GetInt32(2),
            IsManual = reader.GetInt32(3) != 0,
            Confidence = reader.GetDouble(4),
            OffsetMs = reader.IsDBNull(5) ? 0 : reader.GetInt32(5)
        };
    }

    public async Task<HashSet<int>> GetPairedVideoIdsAsync()
    {
        await using var conn = new SqliteConnection(config.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT video_file_id FROM pairings";
        await using var reader = await cmd.ExecuteReaderAsync();
        var ids = new HashSet<int>();
        while (await reader.ReadAsync())
            ids.Add(reader.GetInt32(0));
        return ids;
    }

    public async Task<Dictionary<int, Pairing>> GetAllPairingsAsync()
    {
        await using var conn = new SqliteConnection(config.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, video_file_id, script_file_id, is_manual, confidence, offset_ms
            FROM pairings ORDER BY is_manual DESC, confidence DESC
            """;
        await using var reader = await cmd.ExecuteReaderAsync();
        var result = new Dictionary<int, Pairing>();
        while (await reader.ReadAsync())
        {
            var videoId = reader.GetInt32(1);
            // Keep only the best pairing per video (manual > highest confidence)
            if (!result.ContainsKey(videoId))
            {
                result[videoId] = new Pairing
                {
                    Id = reader.GetInt32(0),
                    VideoFileId = videoId,
                    ScriptFileId = reader.GetInt32(2),
                    IsManual = reader.GetInt32(3) != 0,
                    Confidence = reader.GetDouble(4),
                    OffsetMs = reader.IsDBNull(5) ? 0 : reader.GetInt32(5)
                };
            }
        }
        return result;
    }

    public async Task UpsertAsync(int videoFileId, int scriptFileId, bool isManual, double confidence = 1.0)
    {
        await using var conn = new SqliteConnection(config.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO pairings (video_file_id, script_file_id, is_manual, confidence)
            VALUES (@vid, @sid, @manual, @conf)
            ON CONFLICT(video_file_id, script_file_id) DO UPDATE SET
                is_manual = excluded.is_manual,
                confidence = excluded.confidence
            """;
        cmd.Parameters.AddWithValue("@vid", videoFileId);
        cmd.Parameters.AddWithValue("@sid", scriptFileId);
        cmd.Parameters.AddWithValue("@manual", isManual ? 1 : 0);
        cmd.Parameters.AddWithValue("@conf", confidence);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task UpdateOffsetAsync(int videoFileId, int scriptFileId, int offsetMs)
    {
        await using var conn = new SqliteConnection(config.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE pairings SET offset_ms = @offset
            WHERE video_file_id = @vid AND script_file_id = @sid
            """;
        cmd.Parameters.AddWithValue("@offset", offsetMs);
        cmd.Parameters.AddWithValue("@vid", videoFileId);
        cmd.Parameters.AddWithValue("@sid", scriptFileId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task ClearAutoPairingsAsync()
    {
        await using var conn = new SqliteConnection(config.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM pairings WHERE is_manual = 0";
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DeleteForVideoAsync(int videoFileId)
    {
        await using var conn = new SqliteConnection(config.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM pairings WHERE video_file_id = @vid";
        cmd.Parameters.AddWithValue("@vid", videoFileId);
        await cmd.ExecuteNonQueryAsync();
    }
}
