using HandyPlaylistPlayer.Core.Dispatching;
using HandyPlaylistPlayer.Core.Runtime;

namespace HandyPlaylistPlayer.Core.Features.Queue.ShuffleQueue;

public class ShuffleQueueHandler(IQueueService queue) : ICommandHandler<ShuffleQueueCommand, Unit>
{
    public Task<Unit> HandleAsync(ShuffleQueueCommand command, CancellationToken ct = default)
    {
        queue.Shuffle();
        return Task.FromResult(Unit.Value);
    }
}
