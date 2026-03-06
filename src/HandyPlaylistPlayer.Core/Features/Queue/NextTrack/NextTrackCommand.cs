using HandyPlaylistPlayer.Core.Dispatching;
using HandyPlaylistPlayer.Core.Models;

namespace HandyPlaylistPlayer.Core.Features.Queue.NextTrack;

public record NextTrackCommand : ICommand<QueueItem?>;
