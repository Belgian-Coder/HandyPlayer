using HandyPlaylistPlayer.Core.Dispatching;
using HandyPlaylistPlayer.Core.Runtime;

namespace HandyPlaylistPlayer.Core.Features.Queue.EnqueueItems;

public class EnqueueItemsHandler(IQueueService queue) : ICommandHandler<EnqueueItemsCommand, Unit>
{
    public Task<Unit> HandleAsync(EnqueueItemsCommand command, CancellationToken ct = default)
    {
        queue.Enqueue(command.Items);
        return Task.FromResult(Unit.Value);
    }
}
