using HandyPlaylistPlayer.Core.Models;

namespace HandyPlaylistPlayer.Core.Interfaces;

public interface ILibraryRootRepository
{
    Task<List<LibraryRoot>> GetAllAsync();
    Task<int> AddAsync(string path, string? label = null);
    Task RemoveAsync(int id);
    Task UpdateStatusAsync(int id, string status, DateTime? lastScan = null);
}
