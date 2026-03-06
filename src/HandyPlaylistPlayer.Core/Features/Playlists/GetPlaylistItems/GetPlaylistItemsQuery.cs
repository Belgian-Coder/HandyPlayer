using HandyPlaylistPlayer.Core.Dispatching;
using HandyPlaylistPlayer.Core.Models;

namespace HandyPlaylistPlayer.Core.Features.Playlists.GetPlaylistItems;

public record GetPlaylistItemsQuery(int PlaylistId, Playlist? Playlist = null) : IQuery<List<MediaItem>>;
