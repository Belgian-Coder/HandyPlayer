using HandyPlaylistPlayer.Core.Dispatching;
using HandyPlaylistPlayer.Core.Models;

namespace HandyPlaylistPlayer.Core.Features.Presets.CreatePreset;

public record CreatePresetCommand(Preset Preset) : ICommand<int>;
