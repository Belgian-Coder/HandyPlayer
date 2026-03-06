using HandyPlaylistPlayer.Core.Dispatching;
using HandyPlaylistPlayer.Core.Runtime;

namespace HandyPlaylistPlayer.Core.Features.Playback.Seek;

public class SeekHandler(IPlaybackCoordinator coordinator) : ICommandHandler<SeekCommand, Unit>
{
    public async Task<Unit> HandleAsync(SeekCommand command, CancellationToken ct = default)
    {
        await coordinator.SeekAsync(command.PositionMs);
        return Unit.Value;
    }
}
