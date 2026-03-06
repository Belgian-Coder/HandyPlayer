namespace HandyPlaylistPlayer.Core.Interfaces;

public interface IAppSettingsRepository
{
    Task<string?> GetAsync(string key);
    Task SetAsync(string key, string value);
    Task DeleteAsync(string key);
}
