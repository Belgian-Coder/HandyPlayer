using HandyPlaylistPlayer.Core.Models;

namespace HandyPlaylistPlayer.Core.Interfaces;

public interface IMediaFileRepository
{
    Task<MediaItem?> GetByIdAsync(int id);
    Task<List<MediaItem>> GetByIdsAsync(IEnumerable<int> ids);
    Task<List<MediaItem>> GetAllAsync();
    Task<List<MediaItem>> GetAllVideosAsync();
    Task<List<MediaItem>> GetAllScriptsAsync();
    Task<List<MediaItem>> GetByLibraryRootAsync(int rootId);
    Task<MediaItem?> GetByPathAsync(string fullPath);
    Task<List<MediaItem>> FindByFilenameAsync(string filename, bool isScript);
    Task<int> UpsertAsync(MediaItem item);
    Task UpsertBatchAsync(IReadOnlyList<MediaItem> items);
    Task DeleteByRootAsync(int rootId, IEnumerable<string> exceptPaths);
    Task DeleteByIdAsync(int id);
    Task MarkWatchedAsync(int id, DateTime? watchedAt);
    Task SaveLastPositionAsync(int id, long? positionMs);
    Task UpdateFileHashAsync(int id, string hash);
    Task RelocateAsync(int id, string newFullPath);
}
