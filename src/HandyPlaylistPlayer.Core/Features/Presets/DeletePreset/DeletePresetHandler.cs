using HandyPlaylistPlayer.Core.Dispatching;
using HandyPlaylistPlayer.Core.Interfaces;

namespace HandyPlaylistPlayer.Core.Features.Presets.DeletePreset;

public class DeletePresetHandler(IPresetRepository repo) : ICommandHandler<DeletePresetCommand, Unit>
{
    public async Task<Unit> HandleAsync(DeletePresetCommand command, CancellationToken ct = default)
    {
        await repo.DeleteAsync(command.PresetId);
        return Unit.Value;
    }
}
