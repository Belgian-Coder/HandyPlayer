using HandyPlaylistPlayer.Core.Dispatching;
using HandyPlaylistPlayer.Core.Runtime;

namespace HandyPlaylistPlayer.Core.Features.Queue.ClearQueue;

public class ClearQueueHandler(IQueueService queue) : ICommandHandler<ClearQueueCommand, Unit>
{
    public Task<Unit> HandleAsync(ClearQueueCommand command, CancellationToken ct = default)
    {
        queue.Clear();
        return Task.FromResult(Unit.Value);
    }
}
