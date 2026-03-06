using HandyPlaylistPlayer.Core.Models;

namespace HandyPlaylistPlayer.Core.Interfaces;

public interface IPlaylistRepository
{
    Task<List<Playlist>> GetAllAsync();
    Task<int> CreateAsync(string name, string type = PlaylistTypes.Static, string? folderPath = null);
    Task RenameAsync(int id, string newName);
    Task DeleteAsync(int id);
    Task AddItemAsync(int playlistId, int mediaFileId, int position);
    Task<int> AddItemsBatchAsync(int playlistId, IReadOnlyList<int> mediaFileIds);
    Task RemoveItemAsync(int playlistId, int mediaFileId);
    Task UpdateItemPositionAsync(int playlistId, int mediaFileId, int newPosition);
    Task ReorderItemsAsync(int playlistId, IReadOnlyList<int> mediaFileIdsInOrder);
    Task<List<MediaItem>> GetItemsAsync(int playlistId);
    Task<List<MediaItem>> GetFolderItemsAsync(string folderPath, string sortOrder = SortOrders.Name);
    Task<List<MediaItem>> GetSmartItemsAsync(string filterJson);
    Task UpdateFilterAsync(int id, string? filterJson);
    Task<int> GetItemCountAsync(int playlistId);
    Task<Dictionary<int, int>> GetItemCountsAsync();
    Task<int> RemoveOrphanedItemsAsync();
}
