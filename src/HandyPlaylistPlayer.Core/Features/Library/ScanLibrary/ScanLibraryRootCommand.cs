using HandyPlaylistPlayer.Core.Dispatching;
using HandyPlaylistPlayer.Core.Models;

namespace HandyPlaylistPlayer.Core.Features.Library.ScanLibrary;

public record ScanLibraryRootCommand(int RootId, IProgress<ScanProgress>? Progress = null) : ICommand;
