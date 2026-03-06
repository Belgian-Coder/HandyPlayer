using HandyPlaylistPlayer.Core.Dispatching;
using HandyPlaylistPlayer.Core.Interfaces;

namespace HandyPlaylistPlayer.Core.Features.Playlists.RemovePlaylistItem;

public class RemovePlaylistItemHandler(IPlaylistRepository repo) : ICommandHandler<RemovePlaylistItemCommand, Unit>
{
    public async Task<Unit> HandleAsync(RemovePlaylistItemCommand command, CancellationToken ct = default)
    {
        await repo.RemoveItemAsync(command.PlaylistId, command.MediaFileId);
        return Unit.Value;
    }
}
