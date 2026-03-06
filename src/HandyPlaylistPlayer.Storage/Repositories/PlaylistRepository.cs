using System.Globalization;
using System.Text.Json;
using HandyPlaylistPlayer.Core;
using HandyPlaylistPlayer.Core.Interfaces;
using HandyPlaylistPlayer.Core.Models;
using Microsoft.Data.Sqlite;

namespace HandyPlaylistPlayer.Storage.Repositories;

public class PlaylistRepository(DatabaseConfig config) : IPlaylistRepository
{
    public async Task<List<Playlist>> GetAllAsync()
    {
        await using var conn = new SqliteConnection(config.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM playlists ORDER BY name";
        await using var reader = await cmd.ExecuteReaderAsync();
        var playlists = new List<Playlist>();
        if (!await reader.ReadAsync()) return playlists;

        // Cache ordinals once
        int idOrd = reader.GetOrdinal("id"),
            nameOrd = reader.GetOrdinal("name"),
            typeOrd = reader.GetOrdinal("type"),
            folderOrd = reader.GetOrdinal("folder_path"),
            filterOrd = reader.GetOrdinal("filter_json"),
            sortOrd = reader.GetOrdinal("sort_order"),
            createdOrd = reader.GetOrdinal("created_at"),
            updatedOrd = reader.GetOrdinal("updated_at");

        do
        {
            playlists.Add(new Playlist
            {
                Id = reader.GetInt32(idOrd),
                Name = reader.GetString(nameOrd),
                Type = reader.GetString(typeOrd),
                FolderPath = reader.IsDBNull(folderOrd) ? null : reader.GetString(folderOrd),
                FilterJson = reader.IsDBNull(filterOrd) ? null : reader.GetString(filterOrd),
                SortOrder = reader.GetString(sortOrd),
                CreatedAt = DateTime.Parse(reader.GetString(createdOrd), CultureInfo.InvariantCulture),
                UpdatedAt = DateTime.Parse(reader.GetString(updatedOrd), CultureInfo.InvariantCulture),
            });
        } while (await reader.ReadAsync());

        return playlists;
    }

    public async Task<int> CreateAsync(string name, string type = PlaylistTypes.Static, string? folderPath = null)
    {
        await using var conn = new SqliteConnection(config.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO playlists (name, type, folder_path) VALUES (@name, @type, @folder) RETURNING id
            """;
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@type", type);
        cmd.Parameters.AddWithValue("@folder", (object?)folderPath ?? DBNull.Value);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    public async Task UpdateFilterAsync(int id, string? filterJson)
    {
        await using var conn = new SqliteConnection(config.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE playlists SET filter_json = @filter, updated_at = datetime('now') WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@filter", (object?)filterJson ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task RenameAsync(int id, string newName)
    {
        await using var conn = new SqliteConnection(config.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE playlists SET name = @name, updated_at = datetime('now') WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@name", newName);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DeleteAsync(int id)
    {
        await using var conn = new SqliteConnection(config.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM playlists WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task AddItemAsync(int playlistId, int mediaFileId, int position)
    {
        await using var conn = new SqliteConnection(config.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO playlist_items (playlist_id, media_file_id, position)
            VALUES (@pid, @mid, @pos)
            """;
        cmd.Parameters.AddWithValue("@pid", playlistId);
        cmd.Parameters.AddWithValue("@mid", mediaFileId);
        cmd.Parameters.AddWithValue("@pos", position);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<int> AddItemsBatchAsync(int playlistId, IReadOnlyList<int> mediaFileIds)
    {
        if (mediaFileIds.Count == 0) return 0;
        await using var conn = new SqliteConnection(config.ConnectionString);
        await conn.OpenAsync();
        await using var transaction = await conn.BeginTransactionAsync();
        try
        {
            // Get current max position
            await using var countCmd = conn.CreateCommand();
            countCmd.CommandText = "SELECT COALESCE(MAX(position), -1) + 1 FROM playlist_items WHERE playlist_id = @pid";
            countCmd.Parameters.AddWithValue("@pid", playlistId);
            var startPos = Convert.ToInt32(await countCmd.ExecuteScalarAsync());

            // Build multi-value INSERT in batches of 100 (SQLite max variables = 999)
            int added = 0;
            const int batchSize = 100;
            for (int batch = 0; batch < mediaFileIds.Count; batch += batchSize)
            {
                var count = Math.Min(batchSize, mediaFileIds.Count - batch);
                await using var cmd = conn.CreateCommand();
                var sb = new System.Text.StringBuilder(
                    "INSERT OR IGNORE INTO playlist_items (playlist_id, media_file_id, position) VALUES ");
                for (int i = 0; i < count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(CultureInfo.InvariantCulture, $"(@pid,@m{i},@p{i})");
                }
                cmd.CommandText = sb.ToString();
                cmd.Parameters.AddWithValue("@pid", playlistId);
                for (int i = 0; i < count; i++)
                {
                    cmd.Parameters.AddWithValue($"@m{i}", mediaFileIds[batch + i]);
                    cmd.Parameters.AddWithValue($"@p{i}", startPos + batch + i);
                }
                added += await cmd.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
            return added;
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    public async Task RemoveItemAsync(int playlistId, int mediaFileId)
    {
        await using var conn = new SqliteConnection(config.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM playlist_items WHERE playlist_id = @pid AND media_file_id = @mid";
        cmd.Parameters.AddWithValue("@pid", playlistId);
        cmd.Parameters.AddWithValue("@mid", mediaFileId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task UpdateItemPositionAsync(int playlistId, int mediaFileId, int newPosition)
    {
        await using var conn = new SqliteConnection(config.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE playlist_items SET position = @pos WHERE playlist_id = @pid AND media_file_id = @mid";
        cmd.Parameters.AddWithValue("@pid", playlistId);
        cmd.Parameters.AddWithValue("@mid", mediaFileId);
        cmd.Parameters.AddWithValue("@pos", newPosition);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task ReorderItemsAsync(int playlistId, IReadOnlyList<int> mediaFileIdsInOrder)
    {
        await using var conn = new SqliteConnection(config.ConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        for (int i = 0; i < mediaFileIdsInOrder.Count; i++)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)tx;
            cmd.CommandText = "UPDATE playlist_items SET position = @pos WHERE playlist_id = @pid AND media_file_id = @mid";
            cmd.Parameters.AddWithValue("@pid", playlistId);
            cmd.Parameters.AddWithValue("@mid", mediaFileIdsInOrder[i]);
            cmd.Parameters.AddWithValue("@pos", i);
            await cmd.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
    }

    public async Task<int> GetItemCountAsync(int playlistId)
    {
        await using var conn = new SqliteConnection(config.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM playlist_items WHERE playlist_id = @pid";
        cmd.Parameters.AddWithValue("@pid", playlistId);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    public async Task<Dictionary<int, int>> GetItemCountsAsync()
    {
        await using var conn = new SqliteConnection(config.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT playlist_id, COUNT(*) FROM playlist_items GROUP BY playlist_id";
        var result = new Dictionary<int, int>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result[reader.GetInt32(0)] = reader.GetInt32(1);
        return result;
    }

    public async Task<int> RemoveOrphanedItemsAsync()
    {
        await using var conn = new SqliteConnection(config.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM playlist_items
            WHERE media_file_id NOT IN (SELECT id FROM media_files)
            """;
        return await cmd.ExecuteNonQueryAsync();
    }

    public async Task<List<MediaItem>> GetItemsAsync(int playlistId)
    {
        await using var conn = new SqliteConnection(config.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT mf.* FROM playlist_items pi
            JOIN media_files mf ON mf.id = pi.media_file_id
            WHERE pi.playlist_id = @pid
            ORDER BY pi.position
            """;
        cmd.Parameters.AddWithValue("@pid", playlistId);
        return await ReadMediaItems(cmd);
    }

    public async Task<List<MediaItem>> GetFolderItemsAsync(string folderPath, string sortOrder = SortOrders.Name)
    {
        await using var conn = new SqliteConnection(config.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();

        // Normalize path separator for LIKE query
        var normalizedPath = folderPath.Replace('\\', '/');
        if (!normalizedPath.EndsWith('/')) normalizedPath += '/';

        var orderClause = sortOrder switch
        {
            "date" => "mf.modified_at DESC",
            "size" => "mf.file_size DESC",
            "duration" => "mf.duration_ms DESC",
            _ => "mf.filename"
        };

        cmd.CommandText = $"""
            SELECT mf.* FROM media_files mf
            WHERE mf.is_script = 0
            AND (REPLACE(mf.full_path, '\', '/') LIKE @folder || '%')
            ORDER BY {orderClause}
            """;
        cmd.Parameters.AddWithValue("@folder", normalizedPath);
        return await ReadMediaItems(cmd);
    }

    public async Task<List<MediaItem>> GetSmartItemsAsync(string filterJson)
    {
        await using var conn = new SqliteConnection(config.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();

        var conditions = new List<string> { "mf.is_script = 0" };
        var filter = JsonSerializer.Deserialize<JsonElement>(filterJson);

        if (filter.TryGetProperty("watched", out var watched))
        {
            conditions.Add(watched.GetString() == "yes"
                ? "mf.watched_at IS NOT NULL"
                : "mf.watched_at IS NULL");
        }

        if (filter.TryGetProperty("paired", out var paired))
        {
            var pairedVal = paired.GetString();
            string subquery;
            if (pairedVal == "yes")
                subquery = "EXISTS (SELECT 1 FROM pairings p WHERE p.video_file_id = mf.id)";
            else if (pairedVal == "no")
                subquery = "NOT EXISTS (SELECT 1 FROM pairings p WHERE p.video_file_id = mf.id)";
            else if (pairedVal == "multiple")
                subquery = @"id IN (
                    SELECT video_file_id FROM pairings
                    GROUP BY video_file_id HAVING COUNT(*) > 1)";
            else
                subquery = "NOT EXISTS (SELECT 1 FROM pairings p WHERE p.video_file_id = mf.id)";
            conditions.Add(subquery);
        }

        if (filter.TryGetProperty("nameContains", out var nameContains)
            && nameContains.ValueKind == JsonValueKind.String)
        {
            var nameFilter = nameContains.GetString();
            if (!string.IsNullOrEmpty(nameFilter))
            {
                conditions.Add("mf.filename LIKE @nameFilter");
                cmd.Parameters.AddWithValue("@nameFilter", $"%{nameFilter}%");
            }
        }

        if (filter.TryGetProperty("minDurationMs", out var minDur))
        {
            conditions.Add("mf.duration_ms >= @minDur");
            cmd.Parameters.AddWithValue("@minDur", minDur.GetInt64());
        }

        if (filter.TryGetProperty("maxDurationMs", out var maxDur))
        {
            conditions.Add("mf.duration_ms <= @maxDur");
            cmd.Parameters.AddWithValue("@maxDur", maxDur.GetInt64());
        }

        cmd.CommandText = $"""
            SELECT mf.* FROM media_files mf
            WHERE {string.Join(" AND ", conditions)}
            ORDER BY mf.filename
            """;
        return await ReadMediaItems(cmd);
    }

    private static async Task<List<MediaItem>> ReadMediaItems(SqliteCommand cmd)
    {
        await using var reader = await cmd.ExecuteReaderAsync();
        var items = new List<MediaItem>();
        if (!await reader.ReadAsync()) return items;

        var ord = new MediaFileRepository.MediaItemOrdinals(reader);
        do
        {
            items.Add(MediaFileRepository.ReadMediaItem(reader, ord));
        } while (await reader.ReadAsync());

        return items;
    }
}
