using HandyPlaylistPlayer.Core.Dispatching;
using HandyPlaylistPlayer.Core.Interfaces;
using HandyPlaylistPlayer.Core.Models;

namespace HandyPlaylistPlayer.Core.Features.Library.FindScriptForVideo;

public class FindScriptForVideoHandler(IAutoPairingEngine pairingEngine)
    : IQueryHandler<FindScriptForVideoQuery, MediaItem?>
{
    public async Task<MediaItem?> HandleAsync(FindScriptForVideoQuery query, CancellationToken ct = default)
        => await pairingEngine.FindScriptForVideoAsync(query.VideoFileId, ct);
}
