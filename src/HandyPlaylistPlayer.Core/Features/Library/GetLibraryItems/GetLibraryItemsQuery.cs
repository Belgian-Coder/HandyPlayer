using HandyPlaylistPlayer.Core.Dispatching;
using HandyPlaylistPlayer.Core.Models;

namespace HandyPlaylistPlayer.Core.Features.Library.GetLibraryItems;

public record GetLibraryItemsQuery : IQuery<List<MediaItem>>;
