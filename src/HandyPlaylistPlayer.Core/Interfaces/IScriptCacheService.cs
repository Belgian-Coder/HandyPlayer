namespace HandyPlaylistPlayer.Core.Interfaces;

public interface IScriptCacheService
{
    Task<string?> GetUrlBySha256Async(string sha256);
    Task UpsertAsync(string sha256, string hostedUrl);
}
