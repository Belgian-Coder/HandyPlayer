using HandyPlaylistPlayer.Core.Dispatching;
using HandyPlaylistPlayer.Core.Runtime;

namespace HandyPlaylistPlayer.Core.Features.Device.DisconnectDevice;

public class DisconnectDeviceHandler(IPlaybackCoordinator coordinator) : ICommandHandler<DisconnectDeviceCommand, Unit>
{
    public async Task<Unit> HandleAsync(DisconnectDeviceCommand command, CancellationToken ct = default)
    {
        await coordinator.DisconnectDeviceAsync();
        return Unit.Value;
    }
}
