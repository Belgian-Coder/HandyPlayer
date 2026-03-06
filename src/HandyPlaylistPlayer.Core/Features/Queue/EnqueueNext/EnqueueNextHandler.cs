using HandyPlaylistPlayer.Core.Dispatching;
using HandyPlaylistPlayer.Core.Runtime;

namespace HandyPlaylistPlayer.Core.Features.Queue.EnqueueNext;

public class EnqueueNextHandler(IQueueService queue) : ICommandHandler<EnqueueNextCommand, Unit>
{
    public Task<Unit> HandleAsync(EnqueueNextCommand command, CancellationToken ct = default)
    {
        queue.EnqueueNext(command.Item);
        return Task.FromResult(Unit.Value);
    }
}
