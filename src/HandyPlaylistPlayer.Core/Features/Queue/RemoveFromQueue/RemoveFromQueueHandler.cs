using HandyPlaylistPlayer.Core.Dispatching;
using HandyPlaylistPlayer.Core.Runtime;

namespace HandyPlaylistPlayer.Core.Features.Queue.RemoveFromQueue;

public class RemoveFromQueueHandler(IQueueService queue) : ICommandHandler<RemoveFromQueueCommand, Unit>
{
    public Task<Unit> HandleAsync(RemoveFromQueueCommand command, CancellationToken ct = default)
    {
        queue.Remove(command.Index);
        return Task.FromResult(Unit.Value);
    }
}
