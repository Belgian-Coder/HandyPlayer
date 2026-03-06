using HandyPlaylistPlayer.Core.Dispatching;
using HandyPlaylistPlayer.Core.Interfaces;

namespace HandyPlaylistPlayer.Core.Features.Library.ToggleWatched;

public class ToggleWatchedHandler(IMediaFileRepository mediaRepo) : ICommandHandler<ToggleWatchedCommand, Unit>
{
    public async Task<Unit> HandleAsync(ToggleWatchedCommand command, CancellationToken ct = default)
    {
        var watchedAt = command.IsWatched ? DateTime.UtcNow : (DateTime?)null;
        await mediaRepo.MarkWatchedAsync(command.MediaItemId, watchedAt);
        return Unit.Value;
    }
}
