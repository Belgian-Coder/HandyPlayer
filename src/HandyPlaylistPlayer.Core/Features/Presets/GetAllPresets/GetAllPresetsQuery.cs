using HandyPlaylistPlayer.Core.Dispatching;
using HandyPlaylistPlayer.Core.Models;

namespace HandyPlaylistPlayer.Core.Features.Presets.GetAllPresets;

public record GetAllPresetsQuery : IQuery<List<Preset>>;
