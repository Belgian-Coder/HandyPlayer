using HandyPlaylistPlayer.Core.Dispatching;
using HandyPlaylistPlayer.Core.Models;

namespace HandyPlaylistPlayer.Core.Features.Queue.EnqueueItems;

public record EnqueueItemsCommand(IEnumerable<QueueItem> Items) : ICommand;
