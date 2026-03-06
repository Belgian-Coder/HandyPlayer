using HandyPlaylistPlayer.Core.Dispatching;
using HandyPlaylistPlayer.Core.Models;

namespace HandyPlaylistPlayer.Core.Features.Queue.EnqueueNext;

public record EnqueueNextCommand(QueueItem Item) : ICommand;
