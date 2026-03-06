using HandyPlaylistPlayer.Core.Dispatching;
using HandyPlaylistPlayer.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace HandyPlaylistPlayer.Core.Features.Library.ScanLibrary;

public class ScanLibraryRootHandler(
    ILibraryIndexer indexer,
    IAutoPairingEngine pairingEngine,
    IPlaylistRepository playlistRepo,
    ILogger<ScanLibraryRootHandler> logger) : ICommandHandler<ScanLibraryRootCommand, Unit>
{
    public async Task<Unit> HandleAsync(ScanLibraryRootCommand command, CancellationToken ct = default)
    {
        await indexer.ScanRootAsync(command.RootId, command.Progress, ct);
        await pairingEngine.RunPairingAsync(ct);

        var removed = await playlistRepo.RemoveOrphanedItemsAsync();
        if (removed > 0)
            logger.LogInformation("Removed {Count} orphaned playlist items", removed);

        return Unit.Value;
    }
}
