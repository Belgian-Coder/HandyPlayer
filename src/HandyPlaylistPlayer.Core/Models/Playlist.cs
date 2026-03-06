namespace HandyPlaylistPlayer.Core.Models;

public class Playlist
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = PlaylistTypes.Static;
    public string? FolderPath { get; set; }
    public string? FilterJson { get; set; }
    public string SortOrder { get; set; } = SortOrders.Name;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Transient — populated by batch query, not persisted
    public int ItemCount { get; set; }
}
