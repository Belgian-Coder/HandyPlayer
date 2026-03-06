using HandyPlaylistPlayer.Core.Dispatching;
using HandyPlaylistPlayer.Core.Interfaces;
using HandyPlaylistPlayer.Core.Models;

namespace HandyPlaylistPlayer.Core.Features.Presets.GetAllPresets;

public class GetAllPresetsHandler(IPresetRepository repo) : IQueryHandler<GetAllPresetsQuery, List<Preset>>
{
    public async Task<List<Preset>> HandleAsync(GetAllPresetsQuery query, CancellationToken ct = default)
        => await repo.GetAllAsync();
}
