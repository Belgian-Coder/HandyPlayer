using HandyPlaylistPlayer.Core.Dispatching;
using HandyPlaylistPlayer.Core.Interfaces;
using HandyPlaylistPlayer.Core.Models;

namespace HandyPlaylistPlayer.Core.Features.Library.GetLibraryItems;

public class GetLibraryItemsHandler(IMediaFileRepository repo) : IQueryHandler<GetLibraryItemsQuery, List<MediaItem>>
{
    public async Task<List<MediaItem>> HandleAsync(GetLibraryItemsQuery query, CancellationToken ct = default)
        => await repo.GetAllVideosAsync();
}
