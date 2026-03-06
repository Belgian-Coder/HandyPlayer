using HandyPlaylistPlayer.Core.Dispatching;
using HandyPlaylistPlayer.Core.Interfaces;

namespace HandyPlaylistPlayer.Core.Features.Library.RunAutoPairing;

public class RunAutoPairingHandler(IAutoPairingEngine pairingEngine) : ICommandHandler<RunAutoPairingCommand, Unit>
{
    public async Task<Unit> HandleAsync(RunAutoPairingCommand command, CancellationToken ct = default)
    {
        await pairingEngine.RunPairingAsync(ct);
        return Unit.Value;
    }
}
