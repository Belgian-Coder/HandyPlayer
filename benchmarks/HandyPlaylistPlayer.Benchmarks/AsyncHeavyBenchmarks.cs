using BenchmarkDotNet.Attributes;

namespace HandyPlaylistPlayer.Benchmarks;

/// <summary>
/// Pure async-method overhead, compiled with the same compiler features as the libraries
/// (runtime-async=on for net11.0 unless HandyRuntimeAsync=off). Shows state-machine vs runtime-async cost.
/// </summary>
[MemoryDiagnoser]
public class AsyncHeavyBenchmarks
{
    private const int Iterations = 1_000;
    private const int Depth = 8;

    /// <summary>Awaits a chain of 8 nested async methods that all complete synchronously (typical DI/handler/repo layering).</summary>
    [Benchmark(OperationsPerInvoke = Iterations)]
    public async Task<long> NestedAsync_CompletedSynchronously()
    {
        long sum = 0;
        for (int i = 0; i < Iterations; i++) sum += await Chain(Depth, i);
        return sum;
    }

    private static async Task<long> Chain(int depth, int value)
    {
        if (depth == 0) return value;
        return await Chain(depth - 1, value) + 1;
    }

    [Benchmark(OperationsPerInvoke = Iterations)]
    public async Task<long> NestedValueTask_CompletedSynchronously()
    {
        long sum = 0;
        for (int i = 0; i < Iterations; i++) sum += await ChainVt(Depth, i);
        return sum;
    }

    private static async ValueTask<long> ChainVt(int depth, int value)
    {
        if (depth == 0) return value;
        return await ChainVt(depth - 1, value) + 1;
    }

    /// <summary>Plain no-op awaits in a loop: Task.CompletedTask, ValueTask, and a method returning a result.</summary>
    [Benchmark(OperationsPerInvoke = Iterations)]
    public async Task<int> AwaitNoOps()
    {
        int n = 0;
        for (int i = 0; i < Iterations; i++)
        {
            await Task.CompletedTask;
            await NoOpTask();
            await NoOpValueTask(i);
            n++;
        }
        return n;
    }

    private static async Task NoOpTask() { await Task.CompletedTask; }
    private static async ValueTask NoOpValueTask(int i) { if (i < 0) await Task.Yield(); }

    /// <summary>Chain of 4 async methods where the innermost actually suspends (Task.Yield) - 100 times.</summary>
    [Benchmark(OperationsPerInvoke = 100)]
    public async Task<long> NestedAsync_Suspending()
    {
        long sum = 0;
        for (int i = 0; i < 100; i++) sum += await YieldChain(4, i);
        return sum;
    }

    private static async Task<long> YieldChain(int depth, int value)
    {
        if (depth == 0) { await Task.Yield(); return value; }
        return await YieldChain(depth - 1, value) + 1;
    }

    /// <summary>Async method with exception thrown and caught every call (try/catch/finally in async frames).</summary>
    [Benchmark(OperationsPerInvoke = 100)]
    public async Task<int> AsyncTryCatchFinally()
    {
        int caught = 0;
        for (int i = 0; i < 100; i++)
        {
            try { await Thrower(i); }
            catch (InvalidOperationException) { caught++; }
        }
        return caught;
    }

    private static async Task Thrower(int i)
    {
        try { await Task.CompletedTask; throw new InvalidOperationException("x"); }
        finally { GC.KeepAlive(i.ToString()); }
    }
}
