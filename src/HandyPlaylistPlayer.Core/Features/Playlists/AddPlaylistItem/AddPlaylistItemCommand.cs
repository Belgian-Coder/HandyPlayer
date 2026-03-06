using HandyPlaylistPlayer.Core.Dispatching;

namespace HandyPlaylistPlayer.Core.Features.Playlists.AddPlaylistItem;

public record AddPlaylistItemCommand(int PlaylistId, int MediaFileId, int Position) : ICommand;
