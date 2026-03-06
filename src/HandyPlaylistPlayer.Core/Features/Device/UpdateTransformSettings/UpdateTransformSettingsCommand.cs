using HandyPlaylistPlayer.Core.Dispatching;
using HandyPlaylistPlayer.Core.Models;

namespace HandyPlaylistPlayer.Core.Features.Device.UpdateTransformSettings;

public record UpdateTransformSettingsCommand(TransformSettings Settings) : ICommand;
