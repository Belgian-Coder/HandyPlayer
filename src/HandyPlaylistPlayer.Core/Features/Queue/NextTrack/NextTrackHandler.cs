using HandyPlaylistPlayer.Core.Dispatching;
using HandyPlaylistPlayer.Core.Models;
using HandyPlaylistPlayer.Core.Runtime;

namespace HandyPlaylistPlayer.Core.Features.Queue.NextTrack;

public class NextTrackHandler(IQueueService queue) : ICommandHandler<NextTrackCommand, QueueItem?>
{
    public Task<QueueItem?> HandleAsync(NextTrackCommand command, CancellationToken ct = default)
        => Task.FromResult(queue.Next());
}
