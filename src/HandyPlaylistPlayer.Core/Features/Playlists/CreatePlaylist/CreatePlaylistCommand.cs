using HandyPlaylistPlayer.Core.Dispatching;

namespace HandyPlaylistPlayer.Core.Features.Playlists.CreatePlaylist;

public record CreatePlaylistCommand(string Name, string Type = PlaylistTypes.Static) : ICommand<int>;
