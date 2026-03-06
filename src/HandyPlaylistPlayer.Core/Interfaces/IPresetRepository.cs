using HandyPlaylistPlayer.Core.Models;

namespace HandyPlaylistPlayer.Core.Interfaces;

public interface IPresetRepository
{
    Task<List<Preset>> GetAllAsync();
    Task<int> CreateAsync(Preset preset);
    Task UpdateAsync(Preset preset);
    Task DeleteAsync(int id);
}
