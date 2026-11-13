using System.Collections.Concurrent;

namespace HandyPlaylistPlayer.Core.Dispatching;

/// <summary>
/// Routes commands/queries to their handlers. Per message type a strongly typed pipeline is created once
/// (cached per <c>TResult</c>), so a dispatch is: dictionary lookup → DI resolve → validate → <c>handler.HandleAsync</c>.
/// No expression-tree delegates, no object boxing of results, and no extra async state machine around the handler call.
/// Failures (missing handler, validation, synchronous handler exceptions) are still delivered through the returned task.
/// </summary>
public class Dispatcher(IServiceProvider services) : IDispatcher
{
    public Task<TResult> SendAsync<TResult>(ICommand<TResult> command, CancellationToken ct = default)
    {
        try
        {
            var pipeline = CommandPipelines<TResult>.Cache.GetOrAdd(command.GetType(), CommandPipelines<TResult>.Create);
            return pipeline.SendAsync(services, command, ct);
        }
        catch (OperationCanceledException oce)
        {
            return Canceled<TResult>(oce);
        }
        catch (Exception ex)
        {
            return Task.FromException<TResult>(ex);
        }
    }

    public Task<TResult> QueryAsync<TResult>(IQuery<TResult> query, CancellationToken ct = default)
    {
        try
        {
            var pipeline = QueryPipelines<TResult>.Cache.GetOrAdd(query.GetType(), QueryPipelines<TResult>.Create);
            return pipeline.QueryAsync(services, query, ct);
        }
        catch (OperationCanceledException oce)
        {
            return Canceled<TResult>(oce);
        }
        catch (Exception ex)
        {
            return Task.FromException<TResult>(ex);
        }
    }

    // Keep async-method semantics: a synchronously thrown OperationCanceledException yields a Canceled task, not a Faulted one.
    private static Task<TResult> Canceled<TResult>(OperationCanceledException oce)
    {
        var tcs = new TaskCompletionSource<TResult>();
        tcs.SetCanceled(oce.CancellationToken);
        return tcs.Task;
    }

    // ---- commands ----

    private abstract class CommandPipeline<TResult>
    {
        public abstract Task<TResult> SendAsync(IServiceProvider services, ICommand<TResult> command, CancellationToken ct);
    }

    private sealed class CommandPipeline<TCommand, TResult> : CommandPipeline<TResult>
        where TCommand : ICommand<TResult>
    {
        public override Task<TResult> SendAsync(IServiceProvider services, ICommand<TResult> command, CancellationToken ct)
        {
            var handler = (ICommandHandler<TCommand, TResult>?)services.GetService(typeof(ICommandHandler<TCommand, TResult>))
                ?? throw new InvalidOperationException($"No handler registered for {command.GetType().Name}");

            var typed = (TCommand)command;

            // Run validator if registered
            if (services.GetService(typeof(IValidator<TCommand>)) is IValidator<TCommand> validator)
            {
                var result = validator.Validate(typed);
                if (!result.IsValid)
                    throw new ValidationException(result);
            }

            return handler.HandleAsync(typed, ct);
        }
    }

    private static class CommandPipelines<TResult>
    {
        public static readonly ConcurrentDictionary<Type, CommandPipeline<TResult>> Cache = new();

        public static CommandPipeline<TResult> Create(Type commandType)
            => (CommandPipeline<TResult>)Activator.CreateInstance(
                typeof(CommandPipeline<,>).MakeGenericType(commandType, typeof(TResult)))!;
    }

    // ---- queries ----

    private abstract class QueryPipeline<TResult>
    {
        public abstract Task<TResult> QueryAsync(IServiceProvider services, IQuery<TResult> query, CancellationToken ct);
    }

    private sealed class QueryPipeline<TQuery, TResult> : QueryPipeline<TResult>
        where TQuery : IQuery<TResult>
    {
        public override Task<TResult> QueryAsync(IServiceProvider services, IQuery<TResult> query, CancellationToken ct)
        {
            var handler = (IQueryHandler<TQuery, TResult>?)services.GetService(typeof(IQueryHandler<TQuery, TResult>))
                ?? throw new InvalidOperationException($"No handler registered for {query.GetType().Name}");

            return handler.HandleAsync((TQuery)query, ct);
        }
    }

    private static class QueryPipelines<TResult>
    {
        public static readonly ConcurrentDictionary<Type, QueryPipeline<TResult>> Cache = new();

        public static QueryPipeline<TResult> Create(Type queryType)
            => (QueryPipeline<TResult>)Activator.CreateInstance(
                typeof(QueryPipeline<,>).MakeGenericType(queryType, typeof(TResult)))!;
    }
}
