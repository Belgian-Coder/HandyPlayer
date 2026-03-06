using Microsoft.Extensions.DependencyInjection;

namespace HandyPlaylistPlayer.Core.Dispatching;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddDispatcher(this IServiceCollection services)
    {
        services.AddSingleton<IDispatcher, Dispatcher>();
        return services;
    }

    public static IServiceCollection AddCommandHandler<TCommand, TResult, THandler>(
        this IServiceCollection services)
        where TCommand : ICommand<TResult>
        where THandler : class, ICommandHandler<TCommand, TResult>
    {
        services.AddTransient<ICommandHandler<TCommand, TResult>, THandler>();
        return services;
    }

    public static IServiceCollection AddQueryHandler<TQuery, TResult, THandler>(
        this IServiceCollection services)
        where TQuery : IQuery<TResult>
        where THandler : class, IQueryHandler<TQuery, TResult>
    {
        services.AddTransient<IQueryHandler<TQuery, TResult>, THandler>();
        return services;
    }

    public static IServiceCollection AddValidator<TCommand, TValidator>(
        this IServiceCollection services)
        where TValidator : class, IValidator<TCommand>
    {
        services.AddTransient<IValidator<TCommand>, TValidator>();
        return services;
    }
}
