using HandyPlaylistPlayer.Core.Dispatching;

namespace HandyPlaylistPlayer.Core.Features.Queue.RemoveFromQueue;

public record RemoveFromQueueCommand(int Index) : ICommand;
