using HandyPlaylistPlayer.Core.Dispatching;

namespace HandyPlaylistPlayer.Core.Features.Playlists.DeletePlaylist;

public record DeletePlaylistCommand(int PlaylistId) : ICommand;
