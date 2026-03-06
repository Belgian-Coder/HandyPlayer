using HandyPlaylistPlayer.Core.Dispatching;
using HandyPlaylistPlayer.Core.Runtime;

namespace HandyPlaylistPlayer.Core.Features.Device.ConnectDevice;

public class ConnectDeviceHandler(IPlaybackCoordinator coordinator) : ICommandHandler<ConnectDeviceCommand, Unit>
{
    public async Task<Unit> HandleAsync(ConnectDeviceCommand command, CancellationToken ct = default)
    {
        await coordinator.ConnectDeviceAsync(ct);
        return Unit.Value;
    }
}
