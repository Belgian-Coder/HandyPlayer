using HandyPlaylistPlayer.Core.Dispatching;
using HandyPlaylistPlayer.Core.Interfaces;
using HandyPlaylistPlayer.Core.Models;

namespace HandyPlaylistPlayer.Core.Features.Library.GetLibraryRoots;

public class GetLibraryRootsHandler(ILibraryRootRepository repo) : IQueryHandler<GetLibraryRootsQuery, List<LibraryRoot>>
{
    public async Task<List<LibraryRoot>> HandleAsync(GetLibraryRootsQuery query, CancellationToken ct = default)
        => await repo.GetAllAsync();
}
