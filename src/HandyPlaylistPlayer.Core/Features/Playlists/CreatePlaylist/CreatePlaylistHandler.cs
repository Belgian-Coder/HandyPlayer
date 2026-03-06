using HandyPlaylistPlayer.Core.Dispatching;
using HandyPlaylistPlayer.Core.Interfaces;

namespace HandyPlaylistPlayer.Core.Features.Playlists.CreatePlaylist;

public class CreatePlaylistHandler(IPlaylistRepository repo) : ICommandHandler<CreatePlaylistCommand, int>
{
    public async Task<int> HandleAsync(CreatePlaylistCommand command, CancellationToken ct = default)
        => await repo.CreateAsync(command.Name, command.Type);
}
