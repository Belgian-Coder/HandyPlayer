using BenchmarkDotNet.Attributes;
using HandyPlaylistPlayer.Core.Models;
using HandyPlaylistPlayer.Core.Services;

namespace HandyPlaylistPlayer.Benchmarks;

[MemoryDiagnoser]
public class InterpolatorBenchmarks
{
    private const int Lookups = 10_000;

    [Params(2_000, 50_000)]
    public int ActionCount { get; set; }

    private List<FunscriptAction> _actions = null!;
    private long[] _randomTimes = null!;
    private long[] _sweepTimes = null!;

    [GlobalSetup]
    public void Setup()
    {
        _actions = SyntheticData.BuildActions(ActionCount);
        long duration = _actions[^1].At;
        var rng = new Random(11);
        _randomTimes = new long[Lookups];
        for (int i = 0; i < Lookups; i++) _randomTimes[i] = rng.NextInt64(0, duration);

        // Monotonic sweep like the 10 ms tick engine / UI position updates
        _sweepTimes = new long[Lookups];
        long step = Math.Max(1, duration / Lookups);
        for (int i = 0; i < Lookups; i++) _sweepTimes[i] = i * step;
    }

    [Benchmark(OperationsPerInvoke = Lookups)]
    public long GetPositionAtTime_Random()
    {
        long sum = 0;
        var actions = _actions;
        foreach (var t in _randomTimes) sum += FunscriptInterpolator.GetPositionAtTime(t, actions);
        return sum;
    }

    [Benchmark(OperationsPerInvoke = Lookups)]
    public long GetPositionAtTime_Sweep()
    {
        long sum = 0;
        var actions = _actions;
        foreach (var t in _sweepTimes) sum += FunscriptInterpolator.GetPositionAtTime(t, actions);
        return sum;
    }

    [Benchmark(OperationsPerInvoke = Lookups)]
    public double GetNormalizedPosition_Sweep()
    {
        double sum = 0;
        var actions = _actions;
        foreach (var t in _sweepTimes) sum += FunscriptInterpolator.GetNormalizedPosition(t, actions);
        return sum;
    }

    [Benchmark(OperationsPerInvoke = Lookups)]
    public long GetNextAction_Random()
    {
        long sum = 0;
        var actions = _actions;
        foreach (var t in _randomTimes)
        {
            var next = FunscriptInterpolator.GetNextAction(t, actions);
            if (next.HasValue) sum += next.Value.Index;
        }
        return sum;
    }
}
