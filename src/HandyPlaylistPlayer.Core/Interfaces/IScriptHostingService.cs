namespace HandyPlaylistPlayer.Core.Interfaces;

public interface IScriptHostingService
{
    Task<(string Url, string Sha256)> UploadScriptAsync(string filePath, CancellationToken ct = default);
    Task<string?> GetCachedUrlAsync(string sha256, CancellationToken ct = default);
}
