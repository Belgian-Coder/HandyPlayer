using System.Globalization;
using HandyPlaylistPlayer.Core.Models;
using Microsoft.Data.Sqlite;

namespace HandyPlaylistPlayer.Storage.Repositories;

public class MediaFileRepository(DatabaseConfig config) : HandyPlaylistPlayer.Core.Interfaces.IMediaFileRepository
{
    public async Task<MediaItem?> GetByIdAsync(int id)
    {
        var results = await QueryAsync(
            "SELECT * FROM media_files WHERE id = @id",
            ("@id", id));
        return results.FirstOrDefault();
    }

    public async Task<List<MediaItem>> GetByIdsAsync(IEnumerable<int> ids)
    {
        var idList = ids.ToList();
        if (idList.Count == 0) return [];

        await using var conn = new SqliteConnection(config.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();

        var paramNames = idList.Select((_, i) => $"@id{i}").ToList();
        cmd.CommandText = $"SELECT * FROM media_files WHERE id IN ({string.Join(",", paramNames)})";
        for (int i = 0; i < idList.Count; i++)
            cmd.Parameters.AddWithValue(paramNames[i], idList[i]);

        await using var reader = await cmd.ExecuteReaderAsync();
        var items = new List<MediaItem>();
        if (!await reader.ReadAsync()) return items;
        var ord = new MediaItemOrdinals(reader);
        do { items.Add(ReadMediaItem(reader, ord)); } while (await reader.ReadAsync());
        return items;
    }

    public async Task<List<MediaItem>> GetAllAsync()
    {
        return await QueryAsync("SELECT * FROM media_files ORDER BY filename");
    }

    public async Task<List<MediaItem>> GetAllVideosAsync()
    {
        return await QueryAsync("SELECT * FROM media_files WHERE is_script = 0 ORDER BY filename");
    }

    public async Task<List<MediaItem>> GetAllScriptsAsync()
    {
        return await QueryAsync("SELECT * FROM media_files WHERE is_script = 1 ORDER BY filename");
    }

    public async Task<List<MediaItem>> GetByLibraryRootAsync(int rootId)
    {
        return await QueryAsync(
            "SELECT * FROM media_files WHERE library_root_id = @rootId ORDER BY filename",
            ("@rootId", rootId));
    }

    public async Task<MediaItem?> GetByPathAsync(string fullPath)
    {
        var results = await QueryAsync(
            "SELECT * FROM media_files WHERE full_path = @path",
            ("@path", fullPath));
        return results.FirstOrDefault();
    }

    public async Task<List<MediaItem>> FindByFilenameAsync(string filename, bool isScript)
    {
        return await QueryAsync(
            "SELECT * FROM media_files WHERE filename = @name AND is_script = @is",
            ("@name", filename), ("@is", isScript ? 1 : 0));
    }

    public async Task<int> UpsertAsync(MediaItem item)
    {
        await using var conn = new SqliteConnection(config.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO media_files (library_root_id, full_path, filename, extension, file_size, modified_at, is_script)
            VALUES (@rootId, @path, @name, @ext, @size, @mtime, @is)
            ON CONFLICT(full_path) DO UPDATE SET
                filename = excluded.filename,
                extension = excluded.extension,
                file_size = excluded.file_size,
                modified_at = excluded.modified_at
            RETURNING id
            """;
        cmd.Parameters.AddWithValue("@rootId", item.LibraryRootId);
        cmd.Parameters.AddWithValue("@path", item.FullPath);
        cmd.Parameters.AddWithValue("@name", item.Filename);
        cmd.Parameters.AddWithValue("@ext", item.Extension);
        cmd.Parameters.AddWithValue("@size", item.FileSize.HasValue ? item.FileSize.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@mtime", item.ModifiedAt.HasValue ? item.ModifiedAt.Value.ToString("o") : DBNull.Value);
        cmd.Parameters.AddWithValue("@is", item.IsScript ? 1 : 0);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    public async Task UpsertBatchAsync(IReadOnlyList<MediaItem> items)
    {
        if (items.Count == 0) return;
        await using var conn = new SqliteConnection(config.ConnectionString);
        await conn.OpenAsync();
        await using var transaction = await conn.BeginTransactionAsync();
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO media_files (library_root_id, full_path, filename, extension, file_size, modified_at, is_script)
                VALUES (@rootId, @path, @name, @ext, @size, @mtime, @is)
                ON CONFLICT(full_path) DO UPDATE SET
                    filename = excluded.filename,
                    extension = excluded.extension,
                    file_size = excluded.file_size,
                    modified_at = excluded.modified_at
                """;
            var pRootId = cmd.Parameters.Add("@rootId", SqliteType.Integer);
            var pPath = cmd.Parameters.Add("@path", SqliteType.Text);
            var pName = cmd.Parameters.Add("@name", SqliteType.Text);
            var pExt = cmd.Parameters.Add("@ext", SqliteType.Text);
            var pSize = cmd.Parameters.Add("@size", SqliteType.Integer);
            var pMtime = cmd.Parameters.Add("@mtime", SqliteType.Text);
            var pIs = cmd.Parameters.Add("@is", SqliteType.Integer);
            await cmd.PrepareAsync();

            foreach (var item in items)
            {
                pRootId.Value = item.LibraryRootId;
                pPath.Value = item.FullPath;
                pName.Value = item.Filename;
                pExt.Value = item.Extension;
                pSize.Value = item.FileSize.HasValue ? item.FileSize.Value : DBNull.Value;
                pMtime.Value = item.ModifiedAt.HasValue ? item.ModifiedAt.Value.ToString("o") : DBNull.Value;
                pIs.Value = item.IsScript ? 1 : 0;
                await cmd.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    public async Task DeleteByRootAsync(int rootId, IEnumerable<string> exceptPaths)
    {
        await using var conn = new SqliteConnection(config.ConnectionString);
        await conn.OpenAsync();
        await using var transaction = await conn.BeginTransactionAsync();

        try
        {
            var pathList = exceptPaths.ToList();
            if (pathList.Count == 0)
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM media_files WHERE library_root_id = @rootId";
                cmd.Parameters.AddWithValue("@rootId", rootId);
                await cmd.ExecuteNonQueryAsync();
            }
            else
            {
                // Use temp table to hold paths to keep, then delete everything else
                await using var createCmd = conn.CreateCommand();
                createCmd.CommandText = "CREATE TEMP TABLE _keep_paths (path TEXT)";
                await createCmd.ExecuteNonQueryAsync();

                // Batch insert paths in groups to stay within SQLite limits
                const int batchSize = 500;
                for (int i = 0; i < pathList.Count; i += batchSize)
                {
                    var count = Math.Min(batchSize, pathList.Count - i);
                    await using var insertCmd = conn.CreateCommand();
                    var values = string.Join(",", Enumerable.Range(0, count).Select(idx => $"(@p{idx})"));
                    insertCmd.CommandText = $"INSERT INTO _keep_paths (path) VALUES {values}";
                    for (int j = 0; j < count; j++)
                        insertCmd.Parameters.AddWithValue($"@p{j}", pathList[i + j]);
                    await insertCmd.ExecuteNonQueryAsync();
                }

                await using var deleteCmd = conn.CreateCommand();
                deleteCmd.CommandText = """
                    DELETE FROM media_files
                    WHERE library_root_id = @rootId
                    AND full_path COLLATE NOCASE NOT IN (SELECT path FROM _keep_paths)
                    """;
                deleteCmd.Parameters.AddWithValue("@rootId", rootId);
                await deleteCmd.ExecuteNonQueryAsync();

                await using var dropCmd = conn.CreateCommand();
                dropCmd.CommandText = "DROP TABLE IF EXISTS _keep_paths";
                await dropCmd.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    public async Task DeleteByIdAsync(int id)
    {
        await using var conn = new SqliteConnection(config.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM media_files WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task MarkWatchedAsync(int id, DateTime? watchedAt)
    {
        await using var conn = new SqliteConnection(config.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE media_files SET watched_at = @watchedAt WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@watchedAt", watchedAt.HasValue ? watchedAt.Value.ToString("o") : DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task SaveLastPositionAsync(int id, long? positionMs)
    {
        await using var conn = new SqliteConnection(config.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE media_files SET last_position_ms = @pos WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@pos", positionMs.HasValue ? positionMs.Value : DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task UpdateFileHashAsync(int id, string hash)
    {
        await using var conn = new SqliteConnection(config.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE media_files SET file_hash = @hash WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@hash", hash);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task RelocateAsync(int id, string newFullPath)
    {
        await using var conn = new SqliteConnection(config.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE media_files
            SET full_path = @path, filename = @name, extension = @ext
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@path", newFullPath);
        cmd.Parameters.AddWithValue("@name", Path.GetFileName(newFullPath));
        cmd.Parameters.AddWithValue("@ext",  Path.GetExtension(newFullPath).TrimStart('.').ToLowerInvariant());
        cmd.Parameters.AddWithValue("@id",   id);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<List<MediaItem>> QueryAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = new SqliteConnection(config.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);

        await using var reader = await cmd.ExecuteReaderAsync();
        var items = new List<MediaItem>();
        if (!await reader.ReadAsync()) return items;

        // Cache ordinals once for the result set
        var ord = new MediaItemOrdinals(reader);
        do
        {
            items.Add(ReadMediaItem(reader, ord));
        } while (await reader.ReadAsync());

        return items;
    }

    internal readonly record struct MediaItemOrdinals
    {
        public readonly int Id, LibraryRootId, FullPath, Filename, Extension,
            FileSize, ModifiedAt, DurationMs, IsScript, CreatedAt, WatchedAt, LastPositionMs, FileHash;

        public MediaItemOrdinals(SqliteDataReader reader)
        {
            Id = reader.GetOrdinal("id");
            LibraryRootId = reader.GetOrdinal("library_root_id");
            FullPath = reader.GetOrdinal("full_path");
            Filename = reader.GetOrdinal("filename");
            Extension = reader.GetOrdinal("extension");
            FileSize = reader.GetOrdinal("file_size");
            ModifiedAt = reader.GetOrdinal("modified_at");
            DurationMs = reader.GetOrdinal("duration_ms");
            IsScript = reader.GetOrdinal("is_script");
            CreatedAt = reader.GetOrdinal("created_at");
            WatchedAt = reader.GetOrdinal("watched_at");
            LastPositionMs = reader.GetOrdinal("last_position_ms");
            FileHash = reader.GetOrdinal("file_hash");
        }
    }

    internal static MediaItem ReadMediaItem(SqliteDataReader reader, MediaItemOrdinals ord) => new()
    {
        Id = reader.GetInt32(ord.Id),
        LibraryRootId = reader.GetInt32(ord.LibraryRootId),
        FullPath = reader.GetString(ord.FullPath),
        Filename = reader.GetString(ord.Filename),
        Extension = reader.GetString(ord.Extension),
        FileSize = reader.IsDBNull(ord.FileSize) ? null : reader.GetInt64(ord.FileSize),
        ModifiedAt = reader.IsDBNull(ord.ModifiedAt) ? null : DateTime.Parse(reader.GetString(ord.ModifiedAt), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        DurationMs = reader.IsDBNull(ord.DurationMs) ? null : reader.GetInt64(ord.DurationMs),
        IsScript = reader.GetInt32(ord.IsScript) != 0,
        CreatedAt = DateTime.Parse(reader.GetString(ord.CreatedAt), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        WatchedAt = reader.IsDBNull(ord.WatchedAt) ? null : DateTime.Parse(reader.GetString(ord.WatchedAt), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        LastPositionMs = reader.IsDBNull(ord.LastPositionMs) ? null : reader.GetInt64(ord.LastPositionMs),
        FileHash = reader.IsDBNull(ord.FileHash) ? null : reader.GetString(ord.FileHash)
    };
}
