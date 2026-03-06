namespace HandyPlaylistPlayer.Core.Models;

public class TransformSettings
{
    public int RangeMin { get; set; }
    public int RangeMax { get; set; } = 100;
    public int OffsetMs { get; set; }
    public double? SpeedLimit { get; set; }
    public double? Intensity { get; set; }
    public bool Invert { get; set; }

    /// <summary>Edging: speed threshold (positions/sec) above which movement is reduced.</summary>
    public double? EdgeThreshold { get; set; }

    /// <summary>Edging: factor to reduce movements that exceed the threshold (0.0-1.0).</summary>
    public double EdgeReduction { get; set; } = 0.5;

    public static TransformSettings Default => new();
}
