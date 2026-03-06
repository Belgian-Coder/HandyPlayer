using HandyPlaylistPlayer.Core.Interfaces;
using HandyPlaylistPlayer.Core.Models;
using Microsoft.Data.Sqlite;

namespace HandyPlaylistPlayer.Storage.Repositories;

public class PresetRepository(DatabaseConfig config) : IPresetRepository
{
    public async Task<List<Preset>> GetAllAsync()
    {
        await using var conn = new SqliteConnection(config.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM presets ORDER BY name";
        await using var reader = await cmd.ExecuteReaderAsync();
        var presets = new List<Preset>();
        while (await reader.ReadAsync())
        {
            presets.Add(ReadPreset(reader));
        }
        return presets;
    }

    public async Task<int> CreateAsync(Preset preset)
    {
        await using var conn = new SqliteConnection(config.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO presets (name, device_profile_id, playlist_id, range_min, range_max, offset_ms,
                speed_limit, intensity, invert, is_expert, smoothing_factor, curve_gamma, tick_rate_ms)
            VALUES (@name, @dpid, @plid, @rmin, @rmax, @off, @spd, @int, @inv, @exp, @sf, @cg, @tr)
            RETURNING id
            """;
        AddPresetParams(cmd, preset);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    public async Task UpdateAsync(Preset preset)
    {
        await using var conn = new SqliteConnection(config.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE presets SET name=@name, device_profile_id=@dpid, playlist_id=@plid,
                range_min=@rmin, range_max=@rmax, offset_ms=@off, speed_limit=@spd, intensity=@int,
                invert=@inv, is_expert=@exp, smoothing_factor=@sf, curve_gamma=@cg, tick_rate_ms=@tr
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@id", preset.Id);
        AddPresetParams(cmd, preset);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DeleteAsync(int id)
    {
        await using var conn = new SqliteConnection(config.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM presets WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    private static void AddPresetParams(SqliteCommand cmd, Preset p)
    {
        cmd.Parameters.AddWithValue("@name", p.Name);
        cmd.Parameters.AddWithValue("@dpid", p.DeviceProfileId.HasValue ? p.DeviceProfileId.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@plid", p.PlaylistId.HasValue ? p.PlaylistId.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@rmin", p.RangeMin);
        cmd.Parameters.AddWithValue("@rmax", p.RangeMax);
        cmd.Parameters.AddWithValue("@off", p.OffsetMs);
        cmd.Parameters.AddWithValue("@spd", p.SpeedLimit.HasValue ? p.SpeedLimit.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@int", p.Intensity.HasValue ? p.Intensity.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@inv", p.Invert ? 1 : 0);
        cmd.Parameters.AddWithValue("@exp", p.IsExpert ? 1 : 0);
        cmd.Parameters.AddWithValue("@sf", p.SmoothingFactor);
        cmd.Parameters.AddWithValue("@cg", p.CurveGamma);
        cmd.Parameters.AddWithValue("@tr", p.TickRateMs);
    }

    private static Preset ReadPreset(SqliteDataReader r) => new()
    {
        Id = r.GetInt32(r.GetOrdinal("id")),
        Name = r.GetString(r.GetOrdinal("name")),
        DeviceProfileId = r.IsDBNull(r.GetOrdinal("device_profile_id")) ? null : r.GetInt32(r.GetOrdinal("device_profile_id")),
        PlaylistId = r.IsDBNull(r.GetOrdinal("playlist_id")) ? null : r.GetInt32(r.GetOrdinal("playlist_id")),
        RangeMin = r.GetInt32(r.GetOrdinal("range_min")),
        RangeMax = r.GetInt32(r.GetOrdinal("range_max")),
        OffsetMs = r.GetInt32(r.GetOrdinal("offset_ms")),
        SpeedLimit = r.IsDBNull(r.GetOrdinal("speed_limit")) ? null : r.GetDouble(r.GetOrdinal("speed_limit")),
        Intensity = r.IsDBNull(r.GetOrdinal("intensity")) ? null : r.GetDouble(r.GetOrdinal("intensity")),
        Invert = r.GetInt32(r.GetOrdinal("invert")) != 0,
        IsExpert = r.GetInt32(r.GetOrdinal("is_expert")) != 0,
        SmoothingFactor = r.GetDouble(r.GetOrdinal("smoothing_factor")),
        CurveGamma = r.GetDouble(r.GetOrdinal("curve_gamma")),
        TickRateMs = r.GetInt32(r.GetOrdinal("tick_rate_ms")),
    };
}
