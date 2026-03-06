using HandyPlaylistPlayer.Core.Dispatching;
using HandyPlaylistPlayer.Core.Models;

namespace HandyPlaylistPlayer.Core.Features.Queue.PreviousTrack;

public record PreviousTrackCommand : ICommand<QueueItem?>;
