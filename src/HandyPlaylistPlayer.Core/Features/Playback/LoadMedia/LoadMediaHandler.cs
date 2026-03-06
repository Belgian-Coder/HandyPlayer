using HandyPlaylistPlayer.Core.Dispatching;
using HandyPlaylistPlayer.Core.Runtime;

namespace HandyPlaylistPlayer.Core.Features.Playback.LoadMedia;

public class LoadMediaHandler(IPlaybackCoordinator coordinator) : ICommandHandler<LoadMediaCommand, Unit>
{
    public async Task<Unit> HandleAsync(LoadMediaCommand command, CancellationToken ct = default)
    {
        await coordinator.LoadAsync(command.VideoPath, command.ScriptPath, ct);
        return Unit.Value;
    }
}
