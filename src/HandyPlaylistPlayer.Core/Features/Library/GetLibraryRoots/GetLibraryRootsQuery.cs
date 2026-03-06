using HandyPlaylistPlayer.Core.Dispatching;
using HandyPlaylistPlayer.Core.Models;

namespace HandyPlaylistPlayer.Core.Features.Library.GetLibraryRoots;

public record GetLibraryRootsQuery : IQuery<List<LibraryRoot>>;
