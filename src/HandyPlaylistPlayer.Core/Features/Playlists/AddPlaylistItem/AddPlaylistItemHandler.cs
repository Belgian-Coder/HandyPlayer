using HandyPlaylistPlayer.Core.Dispatching;
using HandyPlaylistPlayer.Core.Interfaces;

namespace HandyPlaylistPlayer.Core.Features.Playlists.AddPlaylistItem;

public class AddPlaylistItemHandler(IPlaylistRepository repo) : ICommandHandler<AddPlaylistItemCommand, Unit>
{
    public async Task<Unit> HandleAsync(AddPlaylistItemCommand command, CancellationToken ct = default)
    {
        await repo.AddItemAsync(command.PlaylistId, command.MediaFileId, command.Position);
        return Unit.Value;
    }
}
