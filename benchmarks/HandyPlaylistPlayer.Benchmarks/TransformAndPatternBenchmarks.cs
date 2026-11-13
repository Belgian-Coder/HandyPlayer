using BenchmarkDotNet.Attributes;
using HandyPlaylistPlayer.Core.Models;
using HandyPlaylistPlayer.Core.Runtime;
using HandyPlaylistPlayer.Core.Services;

namespace HandyPlaylistPlayer.Benchmarks;

[MemoryDiagnoser]
public class ScriptTransformPipelineBenchmarks
{
    private const int Calls = 100_000;

    public enum Config { Default, Full }

    [Params(Config.Default, Config.Full)]
    public Config Settings { get; set; }

    private ScriptTransformPipeline _pipeline = null!;
    private int[] _raw = null!;

    [GlobalSetup]
    public void Setup()
    {
        _pipeline = new ScriptTransformPipeline
        {
            Settings = Settings == Config.Default
                ? TransformSettings.Default
                : new TransformSettings
                {
                    RangeMin = 10, RangeMax = 90, Invert = true, Intensity = 1.25,
                    EdgeThreshold = 250, EdgeReduction = 0.5, SpeedLimit = 400
                }
        };
        var rng = new Random(5);
        _raw = new int[Calls];
        for (int i = 0; i < Calls; i++) _raw[i] = rng.Next(0, 101);
    }

    [Benchmark(OperationsPerInvoke = Calls)]
    public long Transform()
    {
        _pipeline.Reset();
        long sum = 0;
        long t = 1000;
        var raw = _raw;
        var p = _pipeline;
        for (int i = 0; i < raw.Length; i++)
        {
            t += 10;
            sum += p.Transform(raw[i], t);
        }
        return sum;
    }
}

[MemoryDiagnoser]
public class PatternGeneratorBenchmarks
{
    [Params(PatternType.Sine, PatternType.Sawtooth, PatternType.Square, PatternType.Triangle, PatternType.Random)]
    public PatternType Type { get; set; }

    /// <summary>One hour of pattern at 2 Hz.</summary>
    [Benchmark]
    public int Generate_1h() => PatternGenerator.Generate(Type, 2.0, 5, 95, 3_600_000, seed: 1).Actions.Count;

    /// <summary>Typical interactive size: 60 s, regenerated on every slider change.</summary>
    [Benchmark]
    public int Generate_60s() => PatternGenerator.Generate(Type, 1.0, 0, 100, 60_000, seed: 1).Actions.Count;
}
