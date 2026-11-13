using System.Text;
using System.Text.RegularExpressions;
using HandyPlaylistPlayer.Core.Dispatching;
using HandyPlaylistPlayer.Core.Interfaces;
using HandyPlaylistPlayer.Core.Models;
using HandyPlaylistPlayer.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HandyPlaylistPlayer.Tests;

/// <summary>
/// Pins the optimized implementations (span-based normalizer, indexed auto-pairing, typed dispatcher, span interpolator)
/// to the behavior of the straightforward reference implementations they replaced.
/// </summary>
public partial class OptimizationEquivalenceTests
{
    // ---- FilenameNormalizer: reference = the former regex pipeline ----

    private static string ReferenceNormalize(string filename)
    {
        var name = Path.GetFileNameWithoutExtension(filename);
        name = name.ToLowerInvariant();
        name = RefBracket().Replace(name, " ");
        name = RefTags().Replace(name, " ");
        name = RefSeparator().Replace(name, " ");
        name = RefWhitespace().Replace(name, " ");
        return name.Trim();
    }

    [GeneratedRegex(@"\[[^\]]*\]|\([^\)]*\)")] private static partial Regex RefBracket();
    [GeneratedRegex(@"\b(?:1080p|2160p|4k|720p|480p|uhd|hd|sd|x264|x265|h264|h265|hevc|avc|av1|60fps|30fps|120fps|vr|lr|tb|sbs|180|360|trailer|teaser|preview|sample|bluray|bdrip|webrip|webdl|dvdrip|hdtv|aac|ac3|dts|flac|mp3|remastered|extended|uncut|directors)\b", RegexOptions.IgnoreCase)] private static partial Regex RefTags();
    [GeneratedRegex(@"[_\-.]")] private static partial Regex RefSeparator();
    [GeneratedRegex(@"\s+")] private static partial Regex RefWhitespace();

    [Fact]
    public void Normalizer_MatchesRegexReference_OnRealisticNames()
    {
        var normalizer = new FilenameNormalizer();
        var rng = new Random(1234);
        string[] pieces =
        [
            "MyVideo", "Scene", "1080p", "720P", "4K", "x264", "HEVC", "VR", "180", "SBS", "hd", "HDTV", "uhd", "sd", "directors",
            "[Studio]", "(2023)", "[", "]", "(", ")", "[unclosed", "(unclosed", "a(b]c)d", "[a(b]c)", "_", "-", ".", "..", " ", "  ", "\t",
            "720p_x264", "foo_720p", "720p_", "_hd_", "mp3", "dts-hd", "bluray.remastered", "4k60fps", "60fps", "café", "Über",
            "ः", "‍", " ", " ", "İ", "K", "ﬁ", "あい", "x265", "av1", "avc"
        ];

        for (int i = 0; i < 20_000; i++)
        {
            var sb = new StringBuilder();
            int n = rng.Next(0, 9);
            for (int k = 0; k < n; k++) sb.Append(pieces[rng.Next(pieces.Length)]);
            if (rng.Next(3) == 0) sb.Append(".funscript");
            var input = sb.ToString();

            Assert.True(ReferenceNormalize(input) == normalizer.Normalize(input),
                $"Mismatch for input '{input}': reference='{ReferenceNormalize(input)}' actual='{normalizer.Normalize(input)}'");
        }
    }

    [Fact]
    public void Normalizer_HandlesLongNames()
    {
        var normalizer = new FilenameNormalizer();
        var input = string.Concat(Enumerable.Repeat("Some_Long-Name 1080p [tag] ", 80)) + ".mp4";
        Assert.Equal(ReferenceNormalize(input), normalizer.Normalize(input));
    }

    // ---- AutoPairingEngine: reference = the former O(videos x scripts) scan ----

    private sealed record Pair(int Video, int Script, double Confidence);

    private static List<Pair> ReferencePairing(IFilenameNormalizer normalizer, List<MediaItem> videos, List<MediaItem> scripts,
        Dictionary<int, Pairing> existing)
    {
        var result = new List<Pair>();
        foreach (var video in videos)
        {
            if (existing.TryGetValue(video.Id, out var ex) && ex.IsManual) continue;
            var videoDir = Path.GetDirectoryName(video.FullPath) ?? "";
            bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

            var exact = scripts.FirstOrDefault(s => s.Filename.Equals(video.Filename, StringComparison.OrdinalIgnoreCase)
                                                    && Same(Path.GetDirectoryName(s.FullPath), videoDir));
            if (exact != null) { result.Add(new Pair(video.Id, exact.Id, 1.0)); continue; }

            var nv = normalizer.Normalize(video.Filename);
            var sameNorm = scripts.FirstOrDefault(s => Same(Path.GetDirectoryName(s.FullPath), videoDir) && normalizer.Normalize(s.Filename) == nv);
            if (sameNorm != null) { result.Add(new Pair(video.Id, sameNorm.Id, 0.9)); continue; }

            var cross = scripts.FirstOrDefault(s => normalizer.Normalize(s.Filename) == nv);
            if (cross != null) result.Add(new Pair(video.Id, cross.Id, 0.7));
        }
        return result;
    }

    private sealed class RecordingPairingRepo(Dictionary<int, Pairing> existing) : IPairingRepository
    {
        public List<Pair> Upserts { get; } = [];
        public Task<Pairing?> GetForVideoAsync(int videoFileId) => Task.FromResult<Pairing?>(null);
        public Task<HashSet<int>> GetPairedVideoIdsAsync() => Task.FromResult(new HashSet<int>());
        public Task<Dictionary<int, Pairing>> GetAllPairingsAsync() => Task.FromResult(existing);
        public Task UpsertAsync(int videoFileId, int scriptFileId, bool isManual, double confidence = 1.0)
        { Upserts.Add(new Pair(videoFileId, scriptFileId, confidence)); return Task.CompletedTask; }
        public Task ClearAutoPairingsAsync() => Task.CompletedTask;
        public Task DeleteForVideoAsync(int videoFileId) => Task.CompletedTask;
        public Task UpdateOffsetAsync(int videoFileId, int scriptFileId, int offsetMs) => Task.CompletedTask;
    }

    private sealed class FixedMediaRepo(List<MediaItem> videos, List<MediaItem> scripts) : IMediaFileRepository
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

    [Fact]
    public async Task AutoPairing_MatchesLinearScanReference_OnRandomLibraries()
    {
        var normalizer = new FilenameNormalizer();
        string[] folders = ["/a", "/A", "/b", "/b/sub", "/c"];
        string[] stems = ["clip one", "Clip_One", "clip.one", "other", "Other [HD]", "other 1080p", "third", "THIRD", "x", "x 720p"];
        string[] suffixes = ["", " 1080p", "_HD", " (2023)", "[x]"];

        for (int seed = 0; seed < 40; seed++)
        {
            var rng = new Random(seed);
            var videos = new List<MediaItem>();
            var scripts = new List<MediaItem>();
            int id = 1;
            int n = rng.Next(1, 60);
            for (int i = 0; i < n; i++)
            {
                var folder = folders[rng.Next(folders.Length)];
                var name = stems[rng.Next(stems.Length)] + suffixes[rng.Next(suffixes.Length)];
                videos.Add(new MediaItem { Id = id++, FullPath = Path.Combine(folder, name + ".mp4"), Filename = name, IsScript = false });
            }
            int m = rng.Next(0, 60);
            for (int i = 0; i < m; i++)
            {
                var folder = rng.Next(10) == 0 ? "" : folders[rng.Next(folders.Length)];
                var name = stems[rng.Next(stems.Length)] + suffixes[rng.Next(suffixes.Length)];
                scripts.Add(new MediaItem { Id = id++, FullPath = folder.Length == 0 ? name + ".funscript" : Path.Combine(folder, name + ".funscript"), Filename = name, IsScript = true });
            }
            // a root-level script (GetDirectoryName == null) and a manual pairing to skip
            scripts.Add(new MediaItem { Id = id++, FullPath = Path.GetPathRoot(Path.GetFullPath("/")) ?? "/", Filename = "x", IsScript = true });
            var existing = new Dictionary<int, Pairing> { [videos[0].Id] = new Pairing { VideoFileId = videos[0].Id, ScriptFileId = 999, IsManual = true } };

            var expected = ReferencePairing(normalizer, videos, scripts, existing);

            var pairingRepo = new RecordingPairingRepo(existing);
            var engine = new AutoPairingEngine(normalizer, new FixedMediaRepo(videos, scripts), pairingRepo, NullLogger<AutoPairingEngine>.Instance);
            await engine.RunPairingAsync();

            Assert.Equal(expected, pairingRepo.Upserts);
        }
    }

    // ---- Dispatcher ----

    private record Ping(int Value) : ICommand<int>;
    private record Pong(string Text) : IQuery<string>;

    private sealed class PingHandler : ICommandHandler<Ping, int>
    {
        public Task<int> HandleAsync(Ping command, CancellationToken ct = default) => Task.FromResult(command.Value * 2);
    }

    private sealed class ThrowingSyncHandler : ICommandHandler<Ping, int>
    {
        public Task<int> HandleAsync(Ping command, CancellationToken ct = default) => throw new ApplicationException("sync boom");
    }

    private sealed class PongHandler : IQueryHandler<Pong, string>
    {
        public Task<string> HandleAsync(Pong query, CancellationToken ct = default) => Task.FromResult(query.Text + "!");
    }

    private static IDispatcher Build(Action<IServiceCollection> configure)
    {
        var services = new ServiceCollection();
        services.AddDispatcher();
        configure(services);
        return services.BuildServiceProvider().GetRequiredService<IDispatcher>();
    }

    [Fact]
    public async Task Dispatcher_ReportsFailuresThroughTheReturnedTask_NotSynchronously()
    {
        var missing = Build(_ => { });
        Task<int> t1 = missing.SendAsync(new Ping(1)); // must not throw here
        await Assert.ThrowsAsync<InvalidOperationException>(() => t1);

        var throwing = Build(s => s.AddCommandHandler<Ping, int, ThrowingSyncHandler>());
        Task<int> t2 = throwing.SendAsync(new Ping(1)); // synchronous handler exception is also a faulted task
        await Assert.ThrowsAsync<ApplicationException>(() => t2);

        Task<string> t3 = missing.QueryAsync(new Pong("x"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => t3);
    }

    [Fact]
    public async Task Dispatcher_ConcurrentFirstUse_Works()
    {
        var dispatcher = Build(s =>
        {
            s.AddCommandHandler<Ping, int, PingHandler>();
            s.AddQueryHandler<Pong, string, PongHandler>();
        });

        var tasks = Enumerable.Range(0, 200).Select(i => Task.Run(async () =>
            (await dispatcher.SendAsync(new Ping(i))) + (await dispatcher.QueryAsync(new Pong("a"))).Length)).ToArray();
        var results = await Task.WhenAll(tasks);

        for (int i = 0; i < results.Length; i++) Assert.Equal(i * 2 + 2, results[i]);
    }

    // ---- Interpolator: span fast path vs generic IReadOnlyList path ----

    private sealed class ListWrapper(List<FunscriptAction> inner) : IReadOnlyList<FunscriptAction>
    {
        public FunscriptAction this[int index] => inner[index];
        public int Count => inner.Count;
        public IEnumerator<FunscriptAction> GetEnumerator() => inner.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public void Interpolator_FastPathsAgreeWithGenericPath()
    {
        var rng = new Random(9);
        foreach (int count in new[] { 0, 1, 2, 3, 10, 1000 })
        {
            var list = new List<FunscriptAction>();
            long at = 0;
            for (int i = 0; i < count; i++)
            {
                at += rng.Next(0, 300); // includes duplicate timestamps
                list.Add(new FunscriptAction(at, rng.Next(0, 101)));
            }
            var generic = new ListWrapper(list);
            var array = list.ToArray();

            for (long t = -50; t <= at + 50; t += 7)
            {
                Assert.Equal(FunscriptInterpolator.GetPositionAtTime(t, generic), FunscriptInterpolator.GetPositionAtTime(t, list));
                Assert.Equal(FunscriptInterpolator.GetPositionAtTime(t, generic), FunscriptInterpolator.GetPositionAtTime(t, array));
                Assert.Equal(FunscriptInterpolator.GetNextAction(t, generic), FunscriptInterpolator.GetNextAction(t, list));
                Assert.Equal(FunscriptInterpolator.GetNextAction(t, generic), FunscriptInterpolator.GetNextAction(t, array));
            }
        }
    }

    // ---- Parser ----

    [Fact]
    public async Task Parser_AcceptsCommentsTrailingCommasAndAnyPropertyCase()
    {
        var json = """
            { // comment
              "Version": "2.0", "INVERTED": true, "Range": 80, "unknown": {"x": [1,2,3]},
              "Actions": [ {"AT": 300.4, "Pos": 49.6,}, {"at": 100, "pos": 10}, {"at": 100, "pos": 20}, ],
            }
            """;
        var doc = await new FunscriptParser().ParseAsync(new MemoryStream(Encoding.UTF8.GetBytes(json)));

        Assert.Equal("2.0", doc.Version);
        Assert.True(doc.Inverted);
        Assert.Equal(80, doc.Range);
        Assert.Equal(3, doc.Actions.Count);
        Assert.Equal([100L, 100L, 300L], doc.Actions.Select(a => a.At).ToArray());
        Assert.Equal(new FunscriptAction(300, 50), doc.Actions[2]);
    }

    [Fact]
    public async Task Parser_ParseFile_ReadsFromDisk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"parser_{Guid.NewGuid():N}.funscript");
        await File.WriteAllTextAsync(path, """{"actions":[{"at":5,"pos":1},{"at":1,"pos":2}]}""");
        try
        {
            var doc = await new FunscriptParser().ParseFileAsync(path);
            Assert.Equal([1L, 5L], doc.Actions.Select(a => a.At).ToArray());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Parser_InvalidJson_Throws()
    {
        await Assert.ThrowsAnyAsync<System.Text.Json.JsonException>(
            () => new FunscriptParser().ParseAsync(new MemoryStream("{not json"u8.ToArray())));
        await Assert.ThrowsAsync<InvalidDataException>(
            () => new FunscriptParser().ParseAsync(new MemoryStream("null"u8.ToArray())));
    }
}
