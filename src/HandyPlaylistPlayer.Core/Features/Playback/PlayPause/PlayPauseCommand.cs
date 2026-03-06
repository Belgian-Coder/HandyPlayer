using HandyPlaylistPlayer.Core.Dispatching;

namespace HandyPlaylistPlayer.Core.Features.Playback.PlayPause;

public record PlayPauseCommand(bool IsCurrentlyPlaying) : ICommand;
