using BenchmarkDotNet.Attributes;
using HandyPlaylistPlayer.Core.Services;

namespace HandyPlaylistPlayer.Benchmarks;

[MemoryDiagnoser]
public class FunscriptParserBenchmarks
{
    private readonly FunscriptParser _parser = new();
    private byte[][] _demo = null!;
    private byte[] _synthetic20k = null!;
    private byte[] _synthetic200k = null!;
    private string _synthetic20kPath = null!;

    [GlobalSetup]
    public void Setup()
    {
        _demo = SyntheticData.LoadDemoScripts();
        _synthetic20k = SyntheticData.BuildFunscriptJson(20_000);
        _synthetic200k = SyntheticData.BuildFunscriptJson(200_000);
        _synthetic20kPath = Path.Combine(Path.GetTempPath(), "bench_synthetic20k.funscript");
        File.WriteAllBytes(_synthetic20kPath, _synthetic20k);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        try { File.Delete(_synthetic20kPath); } catch { }
    }

    /// <summary>The four demo/*.funscript files, parsed back to back (small-script latency).</summary>
    [Benchmark]
    public async Task<int> Parse_DemoScripts()
    {
        int total = 0;
        foreach (var bytes in _demo)
        {
            using var ms = new MemoryStream(bytes, writable: false);
            total += (await _parser.ParseAsync(ms)).Actions.Count;
        }
        return total;
    }

    /// <summary>Typical long script (~20k actions, ~450 KB).</summary>
    [Benchmark]
    public async Task<int> Parse_Synthetic20k()
    {
        using var ms = new MemoryStream(_synthetic20k, writable: false);
        return (await _parser.ParseAsync(ms)).Actions.Count;
    }

    /// <summary>Stress script (~200k actions, ~4.5 MB).</summary>
    [Benchmark]
    public async Task<int> Parse_Synthetic200k()
    {
        using var ms = new MemoryStream(_synthetic200k, writable: false);
        return (await _parser.ParseAsync(ms)).Actions.Count;
    }

    /// <summary>Real file path: open + parse (includes OS file cache hits).</summary>
    [Benchmark]
    public async Task<int> ParseFile_Synthetic20k()
        => (await _parser.ParseFileAsync(_synthetic20kPath)).Actions.Count;
}
