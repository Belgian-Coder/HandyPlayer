using HandyPlaylistPlayer.Core.Dispatching;
using HandyPlaylistPlayer.Core.Runtime;

namespace HandyPlaylistPlayer.Core.Features.Playback.EmergencyStop;

public class EmergencyStopHandler(IEmergencyStopService emergencyStop) : ICommandHandler<EmergencyStopCommand, Unit>
{
    public async Task<Unit> HandleAsync(EmergencyStopCommand command, CancellationToken ct = default)
    {
        await emergencyStop.TriggerAsync();
        return Unit.Value;
    }
}
