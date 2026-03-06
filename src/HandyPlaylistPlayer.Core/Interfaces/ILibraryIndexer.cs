using HandyPlaylistPlayer.Core.Models;

namespace HandyPlaylistPlayer.Core.Interfaces;

public interface ILibraryIndexer
{
    Task ScanRootAsync(int rootId, IProgress<ScanProgress>? progress = null, CancellationToken ct = default);
    Task ScanAllAsync(IProgress<ScanProgress>? progress = null, CancellationToken ct = default);
}
