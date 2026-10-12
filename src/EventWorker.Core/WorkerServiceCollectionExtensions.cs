using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RobotFleet.Shared.Pipeline;

namespace RobotFleet.EventWorker;

public static class WorkerServiceCollectionExtensions
{
    /// WorkerOptions · EventHandlers · AssignmentCoordinator · WorkerService · ReadinessState · TimeProvider.System 등록.
    /// IEventStore · IEventPublisher · IPartitionSubscriber · IPartitionRoster · IStartupStep은 인프라 확장이 등록한다
    public static IServiceCollection AddWorkerCore(this IServiceCollection services, Action<WorkerOptions>? configure = null)
    {
        var options = new WorkerOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ReadinessState>();
        services.AddSingleton<EventHandlers>();
        services.AddSingleton<AssignmentCoordinator>();
        services.AddHostedService<WorkerService>();
        return services;
    }
}
