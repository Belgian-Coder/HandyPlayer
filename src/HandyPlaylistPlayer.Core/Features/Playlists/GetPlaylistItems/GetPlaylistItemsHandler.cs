using HandyPlaylistPlayer.Core.Dispatching;
using HandyPlaylistPlayer.Core.Interfaces;
using HandyPlaylistPlayer.Core.Models;

namespace HandyPlaylistPlayer.Core.Features.Playlists.GetPlaylistItems;

public class GetPlaylistItemsHandler(IPlaylistRepository repo) : IQueryHandler<GetPlaylistItemsQuery, List<MediaItem>>
{
    public async Task<List<MediaItem>> HandleAsync(GetPlaylistItemsQuery query, CancellationToken ct = default)
    {
        if (query.Playlist is { } pl)
        {
            if (pl.Type == PlaylistTypes.Folder && !string.IsNullOrEmpty(pl.FolderPath))
                return await repo.GetFolderItemsAsync(pl.FolderPath, pl.SortOrder);

            if (pl.Type == PlaylistTypes.Smart && !string.IsNullOrEmpty(pl.FilterJson))
                return await repo.GetSmartItemsAsync(pl.FilterJson);
        }

        return await repo.GetItemsAsync(query.PlaylistId);
    }
}
