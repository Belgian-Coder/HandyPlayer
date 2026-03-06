using HandyPlaylistPlayer.Core.Dispatching;

namespace HandyPlaylistPlayer.Core.Features.Library.RemoveLibraryRoot;

public record RemoveLibraryRootCommand(int RootId) : ICommand;
