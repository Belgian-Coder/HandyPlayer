using HandyPlaylistPlayer.Core.Dispatching;
using HandyPlaylistPlayer.Core.Interfaces;
using HandyPlaylistPlayer.Core.Models;

namespace HandyPlaylistPlayer.Core.Features.Library.GetMissingFiles;

public class GetMissingFilesHandler(IMediaFileRepository mediaRepo) : IQueryHandler<GetMissingFilesQuery, List<MediaItem>>
{
    public async Task<List<MediaItem>> HandleAsync(GetMissingFilesQuery query, CancellationToken ct = default)
    {
        var all = await mediaRepo.GetAllAsync();
        return all.Where(f => !File.Exists(f.FullPath)).ToList();
    }
}
