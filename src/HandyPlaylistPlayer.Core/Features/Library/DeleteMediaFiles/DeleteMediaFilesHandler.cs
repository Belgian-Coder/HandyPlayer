using HandyPlaylistPlayer.Core.Dispatching;
using HandyPlaylistPlayer.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace HandyPlaylistPlayer.Core.Features.Library.DeleteMediaFiles;

public class DeleteMediaFilesHandler(
    IMediaFileRepository mediaRepo,
    IPairingRepository pairingRepo,
    ILogger<DeleteMediaFilesHandler> logger) : ICommandHandler<DeleteMediaFilesCommand, Unit>
{
    public async Task<Unit> HandleAsync(DeleteMediaFilesCommand command, CancellationToken ct = default)
    {
        foreach (var item in command.Items)
        {
            try
            {
                // Delete the physical file first — if this fails (locked, permissions),
                // skip the DB cleanup so the entry stays consistent
                if (File.Exists(item.FullPath))
                {
                    try
                    {
                        File.Delete(item.FullPath);
                    }
                    catch (IOException ex)
                    {
                        logger.LogWarning(ex, "Cannot delete file (may be in use): {Path}", item.FullPath);
                        continue;
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        logger.LogWarning(ex, "No permission to delete file: {Path}", item.FullPath);
                        continue;
                    }
                }

                // File is gone — safe to clean up DB records
                if (!item.IsScript)
                    await pairingRepo.DeleteForVideoAsync(item.Id);

                await mediaRepo.DeleteByIdAsync(item.Id);

                logger.LogInformation("Deleted media file: {Path}", item.FullPath);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to delete media file: {Path}", item.FullPath);
            }
        }

        return Unit.Value;
    }
}
