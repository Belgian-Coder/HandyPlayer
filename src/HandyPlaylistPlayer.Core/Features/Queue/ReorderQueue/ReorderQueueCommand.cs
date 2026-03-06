using HandyPlaylistPlayer.Core.Dispatching;

namespace HandyPlaylistPlayer.Core.Features.Queue.ReorderQueue;

public record ReorderQueueCommand(int OldIndex, int NewIndex) : ICommand;
