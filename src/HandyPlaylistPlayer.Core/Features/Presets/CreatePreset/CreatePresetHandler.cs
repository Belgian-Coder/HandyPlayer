using HandyPlaylistPlayer.Core.Dispatching;
using HandyPlaylistPlayer.Core.Interfaces;

namespace HandyPlaylistPlayer.Core.Features.Presets.CreatePreset;

public class CreatePresetHandler(IPresetRepository repo) : ICommandHandler<CreatePresetCommand, int>
{
    public async Task<int> HandleAsync(CreatePresetCommand command, CancellationToken ct = default)
        => await repo.CreateAsync(command.Preset);
}
