using HandyPlaylistPlayer.Core.Dispatching;
using HandyPlaylistPlayer.Core.Runtime;

namespace HandyPlaylistPlayer.Core.Features.Queue.ReorderQueue;

public class ReorderQueueHandler(IQueueService queue) : ICommandHandler<ReorderQueueCommand, Unit>
{
    public Task<Unit> HandleAsync(ReorderQueueCommand command, CancellationToken ct = default)
    {
        queue.Reorder(command.OldIndex, command.NewIndex);
        return Task.FromResult(Unit.Value);
    }
}
