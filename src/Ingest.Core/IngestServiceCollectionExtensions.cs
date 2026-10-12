using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RobotFleet.Shared.Pipeline;

namespace RobotFleet.Ingest;

public static class IngestServiceCollectionExtensions
{
    /// IngestService · IngestStartupService · ReadinessState(+ rabbitmq.topology 요구) · TimeProvider.System 등록.
    /// IIdemStore · IEventPublisher · IStartupStep은 인프라 확장(AddFleetRabbitMq · AddFleetRedis)이 등록한다
    public static IServiceCollection AddIngestCore(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ReadinessState>();
        services.AddSingleton<IngestService>();
        services.AddHostedService<IngestStartupService>();
        return services;
    }
}
