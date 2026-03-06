using System.Collections.Concurrent;
using System.Linq.Expressions;

namespace HandyPlaylistPlayer.Core.Dispatching;

public class Dispatcher(IServiceProvider services) : IDispatcher
{
    // Cache: commandType -> (handlerServiceType, validatorServiceType, compiled handle delegate, compiled validate delegate)
    private static readonly ConcurrentDictionary<(Type messageType, Type resultType), CachedCommandPipeline> CommandCache = new();
    private static readonly ConcurrentDictionary<(Type queryType, Type resultType), CachedQueryPipeline> QueryCache = new();

    public async Task<TResult> SendAsync<TResult>(ICommand<TResult> command, CancellationToken ct = default)
    {
        var commandType = command.GetType();
        var key = (commandType, typeof(TResult));

        var pipeline = CommandCache.GetOrAdd(key, static k =>
        {
            var handlerServiceType = typeof(ICommandHandler<,>).MakeGenericType(k.messageType, k.resultType);
            var validatorServiceType = typeof(IValidator<>).MakeGenericType(k.messageType);

            // Build compiled delegate for HandleAsync(command, ct) -> Task<TResult>
            var handleDelegate = BuildHandleDelegate(handlerServiceType, k.messageType, k.resultType);

            // Build compiled delegate for Validate(command) -> ValidationResult
            var validateDelegate = BuildValidateDelegate(validatorServiceType, k.messageType);

            return new CachedCommandPipeline(handlerServiceType, validatorServiceType, handleDelegate, validateDelegate);
        });

        var handler = services.GetService(pipeline.HandlerServiceType)
            ?? throw new InvalidOperationException($"No handler registered for {commandType.Name}");

        // Run validator if registered
        var validator = services.GetService(pipeline.ValidatorServiceType);
        if (validator != null)
        {
            var result = pipeline.Validate(validator, command);
            if (!result.IsValid)
                throw new ValidationException(result);
        }

        return await pipeline.Handle<TResult>(handler, command, ct);
    }

    public async Task<TResult> QueryAsync<TResult>(IQuery<TResult> query, CancellationToken ct = default)
    {
        var queryType = query.GetType();
        var key = (queryType, typeof(TResult));

        var pipeline = QueryCache.GetOrAdd(key, static k =>
        {
            var handlerServiceType = typeof(IQueryHandler<,>).MakeGenericType(k.queryType, k.resultType);
            var handleDelegate = BuildHandleDelegate(handlerServiceType, k.queryType, k.resultType);
            return new CachedQueryPipeline(handlerServiceType, handleDelegate);
        });

        var handler = services.GetService(pipeline.HandlerServiceType)
            ?? throw new InvalidOperationException($"No handler registered for {queryType.Name}");

        return await pipeline.Handle<TResult>(handler, query, ct);
    }

    // Builds: (object handler, object command, CancellationToken ct) => (Task<object>)handler.HandleAsync((TCommand)command, ct)
    private static Func<object, object, CancellationToken, Task<object>> BuildHandleDelegate(
        Type handlerType, Type messageType, Type resultType)
    {
        var handlerParam = Expression.Parameter(typeof(object), "handler");
        var messageParam = Expression.Parameter(typeof(object), "message");
        var ctParam = Expression.Parameter(typeof(CancellationToken), "ct");

        var handleMethod = handlerType.GetMethod("HandleAsync")!;

        // ((IHandler)handler).HandleAsync((TMessage)message, ct)
        var call = Expression.Call(
            Expression.Convert(handlerParam, handlerType),
            handleMethod,
            Expression.Convert(messageParam, messageType),
            ctParam);

        // We need to convert Task<TResult> to Task<object>
        // Use a continuation: task.ContinueWith(t => (object)t.Result)
        var taskResultType = typeof(Task<>).MakeGenericType(resultType);
        var continuationMethod = typeof(Dispatcher).GetMethod(nameof(CastTaskResult),
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .MakeGenericMethod(resultType);

        var castCall = Expression.Call(continuationMethod, call);

        return Expression.Lambda<Func<object, object, CancellationToken, Task<object>>>(
            castCall, handlerParam, messageParam, ctParam).Compile();
    }

    private static async Task<object> CastTaskResult<T>(Task<T> task)
    {
        return (await task)!;
    }

    // Builds: (object validator, object command) => ((IValidator<TCommand>)validator).Validate((TCommand)command)
    private static Func<object, object, ValidationResult> BuildValidateDelegate(Type validatorType, Type messageType)
    {
        var validatorParam = Expression.Parameter(typeof(object), "validator");
        var messageParam = Expression.Parameter(typeof(object), "message");

        var validateMethod = validatorType.GetMethod("Validate")!;

        var call = Expression.Call(
            Expression.Convert(validatorParam, validatorType),
            validateMethod,
            Expression.Convert(messageParam, messageType));

        return Expression.Lambda<Func<object, object, ValidationResult>>(
            call, validatorParam, messageParam).Compile();
    }

    private sealed record CachedCommandPipeline(
        Type HandlerServiceType,
        Type ValidatorServiceType,
        Func<object, object, CancellationToken, Task<object>> HandleFunc,
        Func<object, object, ValidationResult> ValidateFunc)
    {
        public async Task<TResult> Handle<TResult>(object handler, object command, CancellationToken ct)
            => (TResult)(await HandleFunc(handler, command, ct));

        public ValidationResult Validate(object validator, object command)
            => ValidateFunc(validator, command);
    }

    private sealed record CachedQueryPipeline(
        Type HandlerServiceType,
        Func<object, object, CancellationToken, Task<object>> HandleFunc)
    {
        public async Task<TResult> Handle<TResult>(object handler, object query, CancellationToken ct)
            => (TResult)(await HandleFunc(handler, query, ct));
    }
}
