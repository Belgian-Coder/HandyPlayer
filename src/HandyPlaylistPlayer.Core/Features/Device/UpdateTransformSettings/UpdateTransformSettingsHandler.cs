using HandyPlaylistPlayer.Core.Dispatching;
using HandyPlaylistPlayer.Core.Runtime;

namespace HandyPlaylistPlayer.Core.Features.Device.UpdateTransformSettings;

public class UpdateTransformSettingsHandler(IPlaybackCoordinator coordinator)
    : ICommandHandler<UpdateTransformSettingsCommand, Unit>
{
    public Task<Unit> HandleAsync(UpdateTransformSettingsCommand command, CancellationToken ct = default)
    {
        coordinator.TransformSettings = command.Settings;
        return Task.FromResult(Unit.Value);
    }
}
