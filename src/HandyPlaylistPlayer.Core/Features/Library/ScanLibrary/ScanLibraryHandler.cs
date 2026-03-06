using HandyPlaylistPlayer.Core.Dispatching;
using HandyPlaylistPlayer.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace HandyPlaylistPlayer.Core.Features.Library.ScanLibrary;

public class ScanLibraryHandler(
    ILibraryIndexer indexer,
    IAutoPairingEngine pairingEngine,
    IPlaylistRepository playlistRepo,
    ILogger<ScanLibraryHandler> logger) : ICommandHandler<ScanLibraryCommand, Unit>
{
    public async Task<Unit> HandleAsync(ScanLibraryCommand command, CancellationToken ct = default)
    {
        await indexer.ScanAllAsync(command.Progress, ct);
        await pairingEngine.RunPairingAsync(ct);

        // Clean up playlist items referencing files that no longer exist in the library
        var removed = await playlistRepo.RemoveOrphanedItemsAsync();
        if (removed > 0)
            logger.LogInformation("Removed {Count} orphaned playlist items", removed);

        return Unit.Value;
    }
}
