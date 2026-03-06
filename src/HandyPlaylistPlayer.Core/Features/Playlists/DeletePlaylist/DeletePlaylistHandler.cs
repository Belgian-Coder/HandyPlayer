using HandyPlaylistPlayer.Core.Dispatching;
using HandyPlaylistPlayer.Core.Interfaces;

namespace HandyPlaylistPlayer.Core.Features.Playlists.DeletePlaylist;

public class DeletePlaylistHandler(IPlaylistRepository repo) : ICommandHandler<DeletePlaylistCommand, Unit>
{
    public async Task<Unit> HandleAsync(DeletePlaylistCommand command, CancellationToken ct = default)
    {
        await repo.DeleteAsync(command.PlaylistId);
        return Unit.Value;
    }
}
