using HandyPlaylistPlayer.Core.Dispatching;
using HandyPlaylistPlayer.Core.Runtime;

namespace HandyPlaylistPlayer.Core.Features.Playback.PlayPause;

public class PlayPauseHandler(IPlaybackCoordinator coordinator) : ICommandHandler<PlayPauseCommand, Unit>
{
    public async Task<Unit> HandleAsync(PlayPauseCommand command, CancellationToken ct = default)
    {
        if (command.IsCurrentlyPlaying)
            await coordinator.PauseAsync();
        else
            await coordinator.PlayAsync();
        return Unit.Value;
    }
}
