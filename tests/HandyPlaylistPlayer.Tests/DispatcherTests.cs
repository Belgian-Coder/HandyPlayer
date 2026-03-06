using HandyPlaylistPlayer.Core.Dispatching;
using HandyPlaylistPlayer.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HandyPlaylistPlayer.Tests;

public class DispatcherTests
{
    // Test command/query types
    private record TestCommand(string Value) : ICommand<string>;
    private record TestVoidCommand(string Value) : ICommand;
    private record TestQuery(int Id) : IQuery<string>;
    private record UnregisteredCommand : ICommand;

    // Test handlers
    private class TestCommandHandler : ICommandHandler<TestCommand, string>
    {
        public Task<string> HandleAsync(TestCommand command, CancellationToken ct = default)
            => Task.FromResult($"handled:{command.Value}");
    }

    private class TestVoidCommandHandler : ICommandHandler<TestVoidCommand, Unit>
    {
        public bool WasCalled { get; private set; }

        public Task<Unit> HandleAsync(TestVoidCommand command, CancellationToken ct = default)
        {
            WasCalled = true;
            return Task.FromResult(Unit.Value);
        }
    }

    private class TestQueryHandler : IQueryHandler<TestQuery, string>
    {
        public Task<string> HandleAsync(TestQuery query, CancellationToken ct = default)
            => Task.FromResult($"result:{query.Id}");
    }

    // Test validators
    private class AlwaysPassValidator : IValidator<TestCommand>
    {
        public ValidationResult Validate(TestCommand instance) => ValidationResult.Success();
    }

    private class AlwaysFailValidator : IValidator<TestCommand>
    {
        public ValidationResult Validate(TestCommand instance)
            => ValidationResult.Failure("Validation failed");
    }

    private static IDispatcher BuildDispatcher(Action<IServiceCollection> configure)
    {
        var services = new ServiceCollection();
        services.AddDispatcher();
        configure(services);
        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IDispatcher>();
    }

    [Fact]
    public async Task SendAsync_ResolvesAndExecutesCommandHandler()
    {
        var dispatcher = BuildDispatcher(s =>
            s.AddCommandHandler<TestCommand, string, TestCommandHandler>());

        var result = await dispatcher.SendAsync(new TestCommand("hello"));

        Assert.Equal("handled:hello", result);
    }

    [Fact]
    public async Task SendAsync_VoidCommand_ReturnsUnit()
    {
        var handler = new TestVoidCommandHandler();
        var dispatcher = BuildDispatcher(s =>
            s.AddSingleton<ICommandHandler<TestVoidCommand, Unit>>(handler));

        var result = await dispatcher.SendAsync(new TestVoidCommand("test"));

        Assert.Equal(Unit.Value, result);
        Assert.True(handler.WasCalled);
    }

    [Fact]
    public async Task QueryAsync_ResolvesAndExecutesQueryHandler()
    {
        var dispatcher = BuildDispatcher(s =>
            s.AddQueryHandler<TestQuery, string, TestQueryHandler>());

        var result = await dispatcher.QueryAsync(new TestQuery(42));

        Assert.Equal("result:42", result);
    }

    [Fact]
    public async Task SendAsync_ThrowsWhenNoHandlerRegistered()
    {
        var dispatcher = BuildDispatcher(_ => { });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => dispatcher.SendAsync(new UnregisteredCommand()));
    }

    [Fact]
    public async Task QueryAsync_ThrowsWhenNoHandlerRegistered()
    {
        var dispatcher = BuildDispatcher(_ => { });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => dispatcher.QueryAsync(new TestQuery(1)));
    }

    [Fact]
    public async Task SendAsync_RunsValidatorBeforeHandler()
    {
        var dispatcher = BuildDispatcher(s =>
        {
            s.AddCommandHandler<TestCommand, string, TestCommandHandler>();
            s.AddValidator<TestCommand, AlwaysFailValidator>();
        });

        var ex = await Assert.ThrowsAsync<ValidationException>(
            () => dispatcher.SendAsync(new TestCommand("test")));

        Assert.Contains("Validation failed", ex.Message);
        Assert.Single(ex.Result.Errors);
    }

    [Fact]
    public async Task SendAsync_PassesWhenValidatorSucceeds()
    {
        var dispatcher = BuildDispatcher(s =>
        {
            s.AddCommandHandler<TestCommand, string, TestCommandHandler>();
            s.AddValidator<TestCommand, AlwaysPassValidator>();
        });

        var result = await dispatcher.SendAsync(new TestCommand("ok"));

        Assert.Equal("handled:ok", result);
    }

    [Fact]
    public async Task SendAsync_SkipsValidationWhenNoValidatorRegistered()
    {
        var dispatcher = BuildDispatcher(s =>
            s.AddCommandHandler<TestCommand, string, TestCommandHandler>());

        var result = await dispatcher.SendAsync(new TestCommand("no-validator"));

        Assert.Equal("handled:no-validator", result);
    }

    [Fact]
    public void ValidationResult_Success_HasNoErrors()
    {
        var result = ValidationResult.Success();

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void ValidationResult_Failure_SingleError()
    {
        var result = ValidationResult.Failure("bad input");

        Assert.False(result.IsValid);
        Assert.Single(result.Errors);
        Assert.Equal("bad input", result.Errors[0]);
    }

    [Fact]
    public void ValidationResult_Failure_MultipleErrors()
    {
        var result = ValidationResult.Failure(["error 1", "error 2"]);

        Assert.False(result.IsValid);
        Assert.Equal(2, result.Errors.Count);
    }

    [Fact]
    public void ValidationException_ContainsAllErrorMessages()
    {
        var result = ValidationResult.Failure(["err1", "err2"]);
        var ex = new ValidationException(result);

        Assert.Contains("err1", ex.Message);
        Assert.Contains("err2", ex.Message);
        Assert.Same(result, ex.Result);
    }

    [Fact]
    public void Unit_DefaultAndValueAreEqual()
    {
        var a = Unit.Value;
        var b = default(Unit);

        Assert.Equal(a, b);
    }
}
