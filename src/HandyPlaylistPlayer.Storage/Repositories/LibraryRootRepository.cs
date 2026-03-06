using System.Globalization;
using HandyPlaylistPlayer.Core.Models;
using Microsoft.Data.Sqlite;

namespace HandyPlaylistPlayer.Storage.Repositories;

public class LibraryRootRepository(DatabaseConfig config) : HandyPlaylistPlayer.Core.Interfaces.ILibraryRootRepository
{
    public async Task<List<LibraryRoot>> GetAllAsync()
    {
        await using var conn = new SqliteConnection(config.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, path, label, is_enabled, last_scan, status FROM library_roots ORDER BY label, path";
        await using var reader = await cmd.ExecuteReaderAsync();
        var roots = new List<LibraryRoot>();
        while (await reader.ReadAsync())
        {
            roots.Add(new LibraryRoot
            {
                Id = reader.GetInt32(0),
                Path = reader.GetString(1),
                Label = reader.IsDBNull(2) ? null : reader.GetString(2),
                IsEnabled = reader.GetInt32(3) != 0,
                LastScan = reader.IsDBNull(4) ? null : DateTime.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
                Status = reader.GetString(5)
            });
        }
        return roots;
    }

    public async Task<int> AddAsync(string path, string? label = null)
    {
        await using var conn = new SqliteConnection(config.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO library_roots (path, label) VALUES (@path, @label) RETURNING id";
        cmd.Parameters.AddWithValue("@path", path);
        cmd.Parameters.AddWithValue("@label", (object?)label ?? DBNull.Value);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    public async Task RemoveAsync(int id)
    {
        await using var conn = new SqliteConnection(config.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM library_roots WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task UpdateStatusAsync(int id, string status, DateTime? lastScan = null)
    {
        await using var conn = new SqliteConnection(config.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE library_roots SET status = @status, last_scan = @scan WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@status", status);
        cmd.Parameters.AddWithValue("@scan", lastScan.HasValue ? lastScan.Value.ToString("o") : DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }
}
