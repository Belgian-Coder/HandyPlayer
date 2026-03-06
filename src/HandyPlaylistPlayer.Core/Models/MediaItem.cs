namespace HandyPlaylistPlayer.Core.Models;

public class MediaItem
{
    public int Id { get; set; }
    public int LibraryRootId { get; set; }
    public string FullPath { get; set; } = string.Empty;
    public string Filename { get; set; } = string.Empty;
    public string Extension { get; set; } = string.Empty;
    public long? FileSize { get; set; }
    public DateTime? ModifiedAt { get; set; }
    public long? DurationMs { get; set; }
    public bool IsScript { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? WatchedAt { get; set; }
    public long? LastPositionMs { get; set; }
    public string? FileHash { get; set; }
}
