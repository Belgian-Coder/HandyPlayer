using HandyPlaylistPlayer.Core.Dispatching;
using HandyPlaylistPlayer.Core.Interfaces;

namespace HandyPlaylistPlayer.Core.Features.Library.UpdatePairingOffset;

public class UpdatePairingOffsetHandler(IPairingRepository pairingRepo) : ICommandHandler<UpdatePairingOffsetCommand, Unit>
{
    public async Task<Unit> HandleAsync(UpdatePairingOffsetCommand command, CancellationToken ct = default)
    {
        await pairingRepo.UpdateOffsetAsync(command.VideoId, command.ScriptId, command.OffsetMs);
        return Unit.Value;
    }
}
