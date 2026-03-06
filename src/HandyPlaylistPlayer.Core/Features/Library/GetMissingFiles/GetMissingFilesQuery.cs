using HandyPlaylistPlayer.Core.Dispatching;
using HandyPlaylistPlayer.Core.Models;

namespace HandyPlaylistPlayer.Core.Features.Library.GetMissingFiles;

public record GetMissingFilesQuery() : IQuery<List<MediaItem>>;
