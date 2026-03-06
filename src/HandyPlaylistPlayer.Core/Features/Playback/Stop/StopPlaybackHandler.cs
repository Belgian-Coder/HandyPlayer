using HandyPlaylistPlayer.Core.Dispatching;
using HandyPlaylistPlayer.Core.Runtime;

namespace HandyPlaylistPlayer.Core.Features.Playback.Stop;

public class StopPlaybackHandler(IPlaybackCoordinator coordinator) : ICommandHandler<StopPlaybackCommand, Unit>
{
    public async Task<Unit> HandleAsync(StopPlaybackCommand command, CancellationToken ct = default)
    {
        await coordinator.StopAsync();
        return Unit.Value;
    }
}
