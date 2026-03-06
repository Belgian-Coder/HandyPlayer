using HandyPlaylistPlayer.Core.Dispatching;
using HandyPlaylistPlayer.Core.Models;

namespace HandyPlaylistPlayer.Core.Features.Library.FindScriptForVideo;

public record FindScriptForVideoQuery(int VideoFileId) : IQuery<MediaItem?>;
