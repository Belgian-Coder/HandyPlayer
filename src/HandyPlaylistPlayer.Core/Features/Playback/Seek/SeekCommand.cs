using HandyPlaylistPlayer.Core.Dispatching;

namespace HandyPlaylistPlayer.Core.Features.Playback.Seek;

public record SeekCommand(long PositionMs) : ICommand;
