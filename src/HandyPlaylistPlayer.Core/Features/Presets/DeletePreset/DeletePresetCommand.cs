using HandyPlaylistPlayer.Core.Dispatching;

namespace HandyPlaylistPlayer.Core.Features.Presets.DeletePreset;

public record DeletePresetCommand(int PresetId) : ICommand;
