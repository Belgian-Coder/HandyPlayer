using HandyPlaylistPlayer.Core.Models;

namespace HandyPlaylistPlayer.Core.Interfaces;

public interface IFunscriptParser
{
    Task<FunscriptDocument> ParseAsync(Stream stream, CancellationToken ct = default);
    Task<FunscriptDocument> ParseFileAsync(string filePath, CancellationToken ct = default);
}
