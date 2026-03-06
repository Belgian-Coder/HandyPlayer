using System.Security.Cryptography;
using HandyPlaylistPlayer.Core.Dispatching;
using HandyPlaylistPlayer.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace HandyPlaylistPlayer.Core.Features.Library.FindDuplicates;

public class FindDuplicatesHandler(
    IMediaFileRepository repo,
    ILogger<FindDuplicatesHandler> logger) : IQueryHandler<FindDuplicatesQuery, List<DuplicateGroup>>
{
    private const int HashBytesCount = 1024 * 1024; // first 1 MB

    public async Task<List<DuplicateGroup>> HandleAsync(FindDuplicatesQuery query, CancellationToken ct = default)
    {
        var allVideos = await repo.GetAllVideosAsync();

        // Step 1: group by file size — only sizes with 2+ files could be duplicates
        var sizeGroups = allVideos
            .Where(v => v.FileSize is > 0)
            .GroupBy(v => v.FileSize!.Value)
            .Where(g => g.Count() > 1)
            .ToList();

        var duplicates = new List<DuplicateGroup>();

        // Step 2: for each size group, compute partial hash to confirm duplicates
        foreach (var sizeGroup in sizeGroups)
        {
            ct.ThrowIfCancellationRequested();

            var hashGroups = new Dictionary<string, List<Models.MediaItem>>();

            foreach (var item in sizeGroup)
            {
                try
                {
                    // Use cached hash if available
                    var hash = item.FileHash;
                    if (string.IsNullOrEmpty(hash))
                    {
                        hash = await ComputePartialHashAsync(item.FullPath, ct);
                        if (hash == null) continue;

                        // Persist the computed hash
                        await repo.UpdateFileHashAsync(item.Id, hash);
                        item.FileHash = hash;
                    }

                    if (!hashGroups.TryGetValue(hash, out var list))
                    {
                        list = [];
                        hashGroups[hash] = list;
                    }
                    list.Add(item);
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Could not hash {Path}", item.FullPath);
                }
            }

            foreach (var (hash, items) in hashGroups)
            {
                if (items.Count > 1)
                {
                    duplicates.Add(new DuplicateGroup
                    {
                        Hash = hash,
                        FileSize = sizeGroup.Key,
                        Items = items
                    });
                }
            }
        }

        return duplicates;
    }

    private static async Task<string?> ComputePartialHashAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return null;

        using var sha = SHA256.Create();
        var buffer = new byte[HashBytesCount];
        int bytesRead;

        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        bytesRead = await fs.ReadAsync(buffer.AsMemory(0, HashBytesCount), ct);

        if (bytesRead == 0) return null;

        var hash = sha.ComputeHash(buffer, 0, bytesRead);
        return Convert.ToHexStringLower(hash);
    }
}
