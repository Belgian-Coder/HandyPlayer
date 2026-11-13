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
        var scriptIndex = new ScriptIndex(normalizer, scripts);

        foreach (var video in videos)
        {
            ct.ThrowIfCancellationRequested();

            if (existingPairings.TryGetValue(video.Id, out var existing) && existing.IsManual)
                continue;

            var match = FindBestScriptMatch(video, scriptIndex);
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

    /// <summary>
    /// Matching indexes built once per pairing run. All lookups keep "first script in list order wins",
    /// which is what the previous per-video linear scans (FirstOrDefault) returned.
    /// </summary>
    private sealed class ScriptIndex
    {
        private readonly IFilenameNormalizer _normalizer;
        private readonly List<MediaItem> _scripts;

        // Rule 1: (directory, filename) – both case-insensitive
        private readonly Dictionary<(string Dir, string Name), MediaItem> _exact =
            new(DirectoryNameComparer.Instance);

        // Rule 2/3 are built lazily (only needed when some video has no exact match)
        private Dictionary<(string Dir, string Name), MediaItem>? _normalizedSameFolder;
        private Dictionary<string, MediaItem>? _normalizedAny;

        public ScriptIndex(IFilenameNormalizer normalizer, List<MediaItem> scripts)
        {
            _normalizer = normalizer;
            _scripts = scripts;

            foreach (var s in scripts)
            {
                // A script whose path has no directory (null) never matched any video folder before; keep that.
                var dir = Path.GetDirectoryName(s.FullPath);
                if (dir != null)
                    _exact.TryAdd((dir, s.Filename), s);
            }
        }

        public MediaItem? FindExact(string videoDir, string videoFilename)
            => _exact.GetValueOrDefault((videoDir, videoFilename));

        public MediaItem? FindNormalizedSameFolder(string videoDir, string normalizedVideo)
        {
            if (_normalizedSameFolder == null)
                BuildNormalizedIndexes();
            return _normalizedSameFolder!.GetValueOrDefault((videoDir, normalizedVideo));
        }

        public MediaItem? FindNormalizedAnywhere(string normalizedVideo)
        {
            if (_normalizedAny == null)
                BuildNormalizedIndexes();
            return _normalizedAny!.GetValueOrDefault(normalizedVideo);
        }

        private void BuildNormalizedIndexes()
        {
            var sameFolder = new Dictionary<(string Dir, string Name), MediaItem>(_scripts.Count, DirectoryNameComparer.Normalized);
            var any = new Dictionary<string, MediaItem>(_scripts.Count, StringComparer.Ordinal);

            foreach (var s in _scripts)
            {
                var normalized = _normalizer.Normalize(s.Filename);
                any.TryAdd(normalized, s);

                var dir = Path.GetDirectoryName(s.FullPath);
                if (dir != null)
                    sameFolder.TryAdd((dir, normalized), s);
            }

            _normalizedSameFolder = sameFolder;
            _normalizedAny = any;
        }
    }

    /// <summary>Directory compared case-insensitively; the name part case-insensitively (exact rule) or ordinally (normalized rules).</summary>
    private sealed class DirectoryNameComparer(StringComparer nameComparer) : IEqualityComparer<(string Dir, string Name)>
    {
        public static readonly DirectoryNameComparer Instance = new(StringComparer.OrdinalIgnoreCase);
        public static readonly DirectoryNameComparer Normalized = new(StringComparer.Ordinal);

        public bool Equals((string Dir, string Name) x, (string Dir, string Name) y) =>
            StringComparer.OrdinalIgnoreCase.Equals(x.Dir, y.Dir) && nameComparer.Equals(x.Name, y.Name);

        public int GetHashCode((string Dir, string Name) obj) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Dir), nameComparer.GetHashCode(obj.Name));
    }

    private (MediaItem Script, double Confidence)? FindBestScriptMatch(MediaItem video, ScriptIndex scripts)
    {
        var videoDir = Path.GetDirectoryName(video.FullPath) ?? "";

        // Rule 1: Same folder, exact basename
        var exactMatch = scripts.FindExact(videoDir, video.Filename);
        if (exactMatch != null)
            return (exactMatch, 1.0);

        // Rule 2: Same folder, normalized basename
        var normalizedVideo = normalizer.Normalize(video.Filename);
        var sameFolderNorm = scripts.FindNormalizedSameFolder(videoDir, normalizedVideo);
        if (sameFolderNorm != null)
            return (sameFolderNorm, 0.9);

        // Rule 3: Cross-folder, normalized
        var crossFolder = scripts.FindNormalizedAnywhere(normalizedVideo);
        if (crossFolder != null)
            return (crossFolder, 0.7);

        return null;
    }
}
