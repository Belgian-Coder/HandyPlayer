using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Running;

namespace HandyPlaylistPlayer.Benchmarks;

public static class Program
{
    // Usage (from any folder):
    //   dotnet run -c Release -f net11.0 --project <repo>\benchmarks\HandyPlaylistPlayer.Benchmarks -- --filter "*" --artifacts <dir>
    // or use benchmarks\run-benchmarks.ps1 (sets HandyMultiTarget=true, which the project requires).
    public static int Main(string[] args)
    {
        IConfig config = DefaultConfig.Instance
            .AddDiagnoser(MemoryDiagnoser.Default)
            .AddExporter(MarkdownExporter.GitHub)
            .AddExporter(JsonExporter.Brief)
            // restoring + building the 4 library projects can exceed BenchmarkDotNet's default 2 minute limit
            .WithBuildTimeout(TimeSpan.FromMinutes(15));

        // Default job: 1 launch, 3 warmup, 12 measured iterations. "--quick" = 1 launch, 3 warmup, 3 iterations.
        bool quick = args.Contains("--quick");
        args = args.Where(a => a != "--quick").ToArray();
        var job = quick
            ? Job.Default.WithLaunchCount(1).WithWarmupCount(3).WithIterationCount(3)
            : Job.Default.WithLaunchCount(1).WithWarmupCount(3).WithIterationCount(12);

        config = config.AddJob(job);

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config);
        return 0;
    }
}
