using HandyPlaylistPlayer.Core.Dispatching;
using HandyPlaylistPlayer.Core.Interfaces;
using HandyPlaylistPlayer.Core.Models;

namespace HandyPlaylistPlayer.Core.Features.Playlists.GetAllPlaylists;

public class GetAllPlaylistsHandler(IPlaylistRepository repo) : IQueryHandler<GetAllPlaylistsQuery, List<Playlist>>
{
    public async Task<List<Playlist>> HandleAsync(GetAllPlaylistsQuery query, CancellationToken ct = default)
        => await repo.GetAllAsync();
}
