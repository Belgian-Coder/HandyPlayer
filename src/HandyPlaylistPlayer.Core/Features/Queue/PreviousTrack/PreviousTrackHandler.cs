using HandyPlaylistPlayer.Core.Dispatching;
using HandyPlaylistPlayer.Core.Models;
using HandyPlaylistPlayer.Core.Runtime;

namespace HandyPlaylistPlayer.Core.Features.Queue.PreviousTrack;

public class PreviousTrackHandler(IQueueService queue) : ICommandHandler<PreviousTrackCommand, QueueItem?>
{
    public Task<QueueItem?> HandleAsync(PreviousTrackCommand command, CancellationToken ct = default)
        => Task.FromResult(queue.Previous());
}
