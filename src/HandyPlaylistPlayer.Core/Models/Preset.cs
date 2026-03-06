namespace HandyPlaylistPlayer.Core.Models;

public class Preset
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int? DeviceProfileId { get; set; }
    public int? PlaylistId { get; set; }
    public int RangeMin { get; set; }
    public int RangeMax { get; set; } = 100;
    public int OffsetMs { get; set; }
    public double? SpeedLimit { get; set; }
    public double? Intensity { get; set; }
    public bool Invert { get; set; }
    public bool IsExpert { get; set; }

    // Advanced fields (stored in DB but previously not mapped)
    public double SmoothingFactor { get; set; } = 0.3;
    public double CurveGamma { get; set; } = 1.0;
    public int TickRateMs { get; set; } = 50;
}
