namespace HandyPlaylistPlayer.Core.Models;

public class PlaybackHistoryEntry
{
    public int Id { get; set; }
    public int MediaFileId { get; set; }
    public string Filename { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; }
    public long DurationMs { get; set; }
}
