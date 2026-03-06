using HandyPlaylistPlayer.Core.Dispatching;

namespace HandyPlaylistPlayer.Core.Features.Library.ToggleWatched;

public record ToggleWatchedCommand(int MediaItemId, bool IsWatched) : ICommand;
