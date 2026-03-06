using HandyPlaylistPlayer.Core.Interfaces;
using HandyPlaylistPlayer.Core.Models;
using Microsoft.Extensions.Logging;

namespace HandyPlaylistPlayer.Core.Services;

public class AutoPairingEngine(
    IFilenameNormalizer normalizer,
    IMediaFileRepository mediaRepo,
    IPairingRepository pairingRepo,
    ILogger<AutoPairingEngine> logger) : IAutoPairingEngine
{
    public async Task RunPairingAsync(CancellationToken ct = default)
    {
        logger.LogInformation("Running auto-pairing...");
        await pairingRepo.ClearAutoPairingsAsync();

        var videos = await mediaRepo.GetAllVideosAsync();
        var scripts = await mediaRepo.GetAllScriptsAsync();

        // Batch fetch all existing pairings once (was N+1: one query per video)
        var existingPairings = await pairingRepo.GetAllPairingsAsync();
        int paired = 0;

        foreach (var video in videos)
        {
            ct.ThrowIfCancellationRequested();

            if (existingPairings.TryGetValue(video.Id, out var existing) && existing.IsManual)
                continue;

            var match = FindBestScriptMatch(video, scripts);
            if (match.HasValue)
            {
                await pairingRepo.UpsertAsync(video.Id, match.Value.Script.Id, false, match.Value.Confidence);
                paired++;
            }
        }

        logger.LogInformation("Auto-pairing complete: {Paired}/{Total} videos paired", paired, videos.Count);
    }

    public async Task<MediaItem?> FindScriptForVideoAsync(int videoFileId, CancellationToken ct = default)
    {
        var pairing = await pairingRepo.GetForVideoAsync(videoFileId);
        if (pairing == null) return null;

        return await mediaRepo.GetByIdAsync(pairing.ScriptFileId);
    }

    private static bool SameDirectory(string? a, string? b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private (MediaItem Script, double Confidence)? FindBestScriptMatch(MediaItem video, List<MediaItem> allScripts)
    {
        var videoDir = Path.GetDirectoryName(video.FullPath) ?? "";

        // Rule 1: Same folder, exact basename
        var exactMatch = allScripts.FirstOrDefault(s =>
            s.Filename.Equals(video.Filename, StringComparison.OrdinalIgnoreCase) &&
            SameDirectory(Path.GetDirectoryName(s.FullPath), videoDir));
        if (exactMatch != null)
            return (exactMatch, 1.0);

        // Rule 2: Same folder, normalized basename
        var normalizedVideo = normalizer.Normalize(video.Filename);
        var sameFolderNorm = allScripts.FirstOrDefault(s =>
            SameDirectory(Path.GetDirectoryName(s.FullPath), videoDir) &&
            normalizer.Normalize(s.Filename) == normalizedVideo);
        if (sameFolderNorm != null)
            return (sameFolderNorm, 0.9);

        // Rule 3: Cross-folder, normalized
        var crossFolder = allScripts.FirstOrDefault(s =>
            normalizer.Normalize(s.Filename) == normalizedVideo);
        if (crossFolder != null)
            return (crossFolder, 0.7);

        return null;
    }
}
