using HandyPlaylistPlayer.Core.Dispatching;
using HandyPlaylistPlayer.Core.Interfaces;

namespace HandyPlaylistPlayer.Core.Features.Library.RemoveLibraryRoot;

public class RemoveLibraryRootHandler(ILibraryRootRepository repo) : ICommandHandler<RemoveLibraryRootCommand, Unit>
{
    public async Task<Unit> HandleAsync(RemoveLibraryRootCommand command, CancellationToken ct = default)
    {
        await repo.RemoveAsync(command.RootId);
        return Unit.Value;
    }
}
