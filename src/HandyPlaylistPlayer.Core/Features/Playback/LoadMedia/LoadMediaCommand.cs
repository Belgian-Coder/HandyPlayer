using HandyPlaylistPlayer.Core.Dispatching;

namespace HandyPlaylistPlayer.Core.Features.Playback.LoadMedia;

public record LoadMediaCommand(string VideoPath, string? ScriptPath) : ICommand;
