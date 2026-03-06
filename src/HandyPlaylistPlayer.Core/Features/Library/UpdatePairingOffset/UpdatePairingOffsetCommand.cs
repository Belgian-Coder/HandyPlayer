using HandyPlaylistPlayer.Core.Dispatching;

namespace HandyPlaylistPlayer.Core.Features.Library.UpdatePairingOffset;

public record UpdatePairingOffsetCommand(int VideoId, int ScriptId, int OffsetMs) : ICommand;
