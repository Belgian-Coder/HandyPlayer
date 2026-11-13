using BenchmarkDotNet.Attributes;
using HandyPlaylistPlayer.Core.Interfaces;
using HandyPlaylistPlayer.Core.Models;
using HandyPlaylistPlayer.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace HandyPlaylistPlayer.Benchmarks;

[MemoryDiagnoser]
public class FilenameNormalizerBenchmarks
{
    private readonly FilenameNormalizer _normalizer = new();
    private string[] _names = null!;

    [GlobalSetup]
    public void Setup() => _names = SyntheticData.BuildFilenames(5_000);

    /// <summary>Normalizes 5,000 file names.</summary>
    [Benchmark(OperationsPerInvoke = 5_000)]
    public int Normalize_5k()
    {
        int total = 0;
        foreach (var n in _names) total += _normalizer.Normalize(n).Length;
        return total;
    }
}

/// <summary>Auto-pairing end to end against in-memory repositories (pure CPU, no DB).</summary>
[MemoryDiagnoser]
public class AutoPairingBenchmarks
{
    [Params(1_000, 5_000)]
    public int Count { get; set; }

    private AutoPairingEngine _engine = null!;
    private CountingPairingRepo _pairings = null!;

    [GlobalSetup]
    public void Setup()
    {
        var (videos, scripts) = SyntheticData.BuildPairingWorkload(Count);
        _pairings = new CountingPairingRepo();
        _engine = new AutoPairingEngine(
            new FilenameNormalizer(),
            new StubMediaRepo(videos, scripts),
            _pairings,
            NullLogger<AutoPairingEngine>.Instance);
    }

    [Benchmark]
    public async Task<int> RunPairing()
    {
        _pairings.Count = 0;
        await _engine.RunPairingAsync();
        return _pairings.Count;
    }

    private sealed class CountingPairingRepo : IPairingRepository
    {
        private static readonly Dictionary<int, Pairing> Empty = new();
        public int Count;
        public Task<Pairing?> GetForVideoAsync(int videoFileId) => Task.FromResult<Pairing?>(null);
        public Task<HashSet<int>> GetPairedVideoIdsAsync() => Task.FromResult(new HashSet<int>());
        public Task<Dictionary<int, Pairing>> GetAllPairingsAsync() => Task.FromResult(Empty);
        public Task UpsertAsync(int videoFileId, int scriptFileId, bool isManual, double confidence = 1.0)
        { Count++; return Task.CompletedTask; }
        public Task ClearAutoPairingsAsync() => Task.CompletedTask;
        public Task DeleteForVideoAsync(int videoFileId) => Task.CompletedTask;
        public Task UpdateOffsetAsync(int videoFileId, int scriptFileId, int offsetMs) => Task.CompletedTask;
    }

    private sealed class StubMediaRepo(List<MediaItem> videos, List<MediaItem> scripts) : IMediaFileRepository
    {
        public Task<List<MediaItem>> GetAllVideosAsync() => Task.FromResult(videos);
        public Task<List<MediaItem>> GetAllScriptsAsync() => Task.FromResult(scripts);
        public Task<MediaItem?> GetByIdAsync(int id) => throw new NotSupportedException();
        public Task<List<MediaItem>> GetByIdsAsync(IEnumerable<int> ids) => throw new NotSupportedException();
        public Task<List<MediaItem>> GetAllAsync() => throw new NotSupportedException();
        public Task<List<MediaItem>> GetByLibraryRootAsync(int rootId) => throw new NotSupportedException();
        public Task<MediaItem?> GetByPathAsync(string fullPath) => throw new NotSupportedException();
        public Task<List<MediaItem>> FindByFilenameAsync(string filename, bool isScript) => throw new NotSupportedException();
        public Task<int> UpsertAsync(MediaItem item) => throw new NotSupportedException();
        public Task UpsertBatchAsync(IReadOnlyList<MediaItem> items) => throw new NotSupportedException();
        public Task DeleteByRootAsync(int rootId, IEnumerable<string> exceptPaths) => throw new NotSupportedException();
        public Task DeleteByIdAsync(int id) => throw new NotSupportedException();
        public Task MarkWatchedAsync(int id, DateTime? watchedAt) => throw new NotSupportedException();
        public Task SaveLastPositionAsync(int id, long? positionMs) => throw new NotSupportedException();
        public Task UpdateFileHashAsync(int id, string hash) => throw new NotSupportedException();
        public Task RelocateAsync(int id, string newFullPath) => throw new NotSupportedException();
    }
}
