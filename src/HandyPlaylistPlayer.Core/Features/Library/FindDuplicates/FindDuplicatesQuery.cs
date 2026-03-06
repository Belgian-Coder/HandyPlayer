using HandyPlaylistPlayer.Core.Dispatching;
using HandyPlaylistPlayer.Core.Models;

namespace HandyPlaylistPlayer.Core.Features.Library.FindDuplicates;

public record FindDuplicatesQuery : IQuery<List<DuplicateGroup>>;

public class DuplicateGroup
{
    public string Hash { get; init; } = string.Empty;
    public long FileSize { get; init; }
    public List<MediaItem> Items { get; init; } = [];
}
