using HandyPlaylistPlayer.Core.Dispatching;
using HandyPlaylistPlayer.Core.Interfaces;

namespace HandyPlaylistPlayer.Core.Features.Library.AddLibraryRoot;

public class AddLibraryRootHandler(ILibraryRootRepository repo) : ICommandHandler<AddLibraryRootCommand, int>
{
    public async Task<int> HandleAsync(AddLibraryRootCommand command, CancellationToken ct = default)
        => await repo.AddAsync(command.Path);
}
