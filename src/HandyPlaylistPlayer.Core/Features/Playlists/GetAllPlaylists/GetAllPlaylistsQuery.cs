using HandyPlaylistPlayer.Core.Dispatching;
using HandyPlaylistPlayer.Core.Models;

namespace HandyPlaylistPlayer.Core.Features.Playlists.GetAllPlaylists;

public record GetAllPlaylistsQuery : IQuery<List<Playlist>>;
