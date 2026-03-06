using HandyPlaylistPlayer.Core.Models;

namespace HandyPlaylistPlayer.Core.Interfaces;

public interface IPlaybackHistoryRepository
{
    Task<int> RecordAsync(int mediaFileId, long durationMs);
    Task<List<PlaybackHistoryEntry>> GetRecentAsync(int limit = 50);
    Task<int> GetTotalPlayCountAsync();
    Task<long> GetTotalWatchTimeAsync();
}
