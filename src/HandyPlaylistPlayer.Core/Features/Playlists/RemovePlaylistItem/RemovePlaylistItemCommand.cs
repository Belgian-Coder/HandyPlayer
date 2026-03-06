using HandyPlaylistPlayer.Core.Dispatching;

namespace HandyPlaylistPlayer.Core.Features.Playlists.RemovePlaylistItem;

public record RemovePlaylistItemCommand(int PlaylistId, int MediaFileId) : ICommand;
