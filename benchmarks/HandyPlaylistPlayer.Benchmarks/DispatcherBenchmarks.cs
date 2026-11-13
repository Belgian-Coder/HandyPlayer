using BenchmarkDotNet.Attributes;
using HandyPlaylistPlayer.Core.Dispatching;
using Microsoft.Extensions.DependencyInjection;

namespace HandyPlaylistPlayer.Benchmarks;

public record AddCommand(int A, int B) : ICommand<int>;
public record NoopCommand : ICommand;
public record YieldCommand(int Value) : ICommand<int>;
public record EchoQuery(int Id) : IQuery<string>;

public class AddHandler : ICommandHandler<AddCommand, int>
{
    public Task<int> HandleAsync(AddCommand command, CancellationToken ct = default) => Task.FromResult(command.A + command.B);
}

public class NoopHandler : ICommandHandler<NoopCommand, Unit>
{
    public Task<Unit> HandleAsync(NoopCommand command, CancellationToken ct = default) => Task.FromResult(Unit.Value);
}

public class YieldHandler : ICommandHandler<YieldCommand, int>
{
    public async Task<int> HandleAsync(YieldCommand command, CancellationToken ct = default)
    {
        await Task.Yield();
        return command.Value + 1;
    }
}

public class EchoHandler : IQueryHandler<EchoQuery, string>
{
    private static readonly string[] Cache = Enumerable.Range(0, 64).Select(i => i.ToString()).ToArray();
    public Task<string> HandleAsync(EchoQuery query, CancellationToken ct = default) => Task.FromResult(Cache[query.Id & 63]);
}

public class AddValidator : IValidator<AddCommand>
{
    public ValidationResult Validate(AddCommand instance) => ValidationResult.Success();
}

/// <summary>Per-message overhead of the CQRS dispatcher (resolution + validator + handler), handlers complete synchronously.</summary>
[MemoryDiagnoser]
public class DispatcherBenchmarks
{
    private const int Ops = 1_000;
    private IDispatcher _dispatcher = null!;
    private ServiceProvider _provider = null!;

    [GlobalSetup]
    public void Setup()
    {
        var services = new ServiceCollection();
        services.AddDispatcher();
        services.AddCommandHandler<AddCommand, int, AddHandler>();
        services.AddValidator<AddCommand, AddValidator>();
        services.AddCommandHandler<NoopCommand, Unit, NoopHandler>();
        services.AddCommandHandler<YieldCommand, int, YieldHandler>();
        services.AddQueryHandler<EchoQuery, string, EchoHandler>();
        _provider = services.BuildServiceProvider();
        _dispatcher = _provider.GetRequiredService<IDispatcher>();
    }

    [GlobalCleanup]
    public void Cleanup() => _provider.Dispose();

    [Benchmark(OperationsPerInvoke = Ops, Baseline = true)]
    public async Task<int> Send_WithValidator()
    {
        int sum = 0;
        for (int i = 0; i < Ops; i++) sum += await _dispatcher.SendAsync(new AddCommand(i, 1));
        return sum;
    }

    [Benchmark(OperationsPerInvoke = Ops)]
    public async Task<int> Send_NoValidator_Unit()
    {
        for (int i = 0; i < Ops; i++) await _dispatcher.SendAsync(new NoopCommand());
        return Ops;
    }

    [Benchmark(OperationsPerInvoke = Ops)]
    public async Task<int> Query()
    {
        int len = 0;
        for (int i = 0; i < Ops; i++) len += (await _dispatcher.QueryAsync(new EchoQuery(i))).Length;
        return len;
    }

    /// <summary>Handler really suspends (Task.Yield) - exercises continuation / thread-pool path through the dispatcher.</summary>
    [Benchmark(OperationsPerInvoke = 100)]
    public async Task<int> Send_HandlerYields()
    {
        int sum = 0;
        for (int i = 0; i < 100; i++) sum += await _dispatcher.SendAsync(new YieldCommand(i));
        return sum;
    }

    /// <summary>500 concurrent sends (WhenAll) of yielding handlers.</summary>
    [Benchmark(OperationsPerInvoke = 500)]
    public async Task<int> Send_FanOut_500()
    {
        var tasks = new Task<int>[500];
        for (int i = 0; i < tasks.Length; i++) tasks[i] = _dispatcher.SendAsync(new YieldCommand(i));
        var results = await Task.WhenAll(tasks);
        return results.Length;
    }
}
