using HandyPlaylistPlayer.Core.Dispatching;

namespace HandyPlaylistPlayer.Core.Features.Library.AddLibraryRoot;

public record AddLibraryRootCommand(string Path) : ICommand<int>;
