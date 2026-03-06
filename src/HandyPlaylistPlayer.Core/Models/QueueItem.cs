namespace HandyPlaylistPlayer.Core.Models;

public class QueueItem
{
    public required MediaItem Video { get; set; }
    public MediaItem? Script { get; set; }
    public int QueueIndex { get; set; }
}
