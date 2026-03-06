namespace HandyPlaylistPlayer.Core.Models;

public class Pairing
{
    public int Id { get; set; }
    public int VideoFileId { get; set; }
    public int ScriptFileId { get; set; }
    public bool IsManual { get; set; }
    public double Confidence { get; set; } = 1.0;
    public int OffsetMs { get; set; }
}
