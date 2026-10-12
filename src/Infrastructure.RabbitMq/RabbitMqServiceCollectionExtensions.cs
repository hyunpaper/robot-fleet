using Microsoft.Extensions.DependencyInjection;
using RobotFleet.Shared.Pipeline;

namespace RobotFleet.Infrastructure.RabbitMq;

public static class RabbitMqServiceCollectionExtensions
{
    /// RabbitConnectionProvider · TopologyDeclarer(IStartupStep) · RabbitEventPublisher(IEventPublisher) ·
    /// RabbitPartitionSubscriber(IPartitionSubscriber) 등록. ConnectionString이 비면 InvalidOperationException
    public static IServiceCollection AddFleetRabbitMq(this IServiceCollection services, Action<RabbitMqOptions> configure)
    {
        var options = new RabbitMqOptions();
        configure(options);
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
            throw new InvalidOperationException(
                "ConnectionStrings:RabbitMq가 비어 있다 (appsettings.Local.json 또는 ConnectionStrings__RabbitMq)");

        services.AddSingleton(options);
        services.AddSingleton<RabbitConnectionProvider>();
        services.AddSingleton<TopologyDeclarer>();
        services.AddSingleton<IStartupStep>(sp => sp.GetRequiredService<TopologyDeclarer>());
        services.AddSingleton<RabbitEventPublisher>();
        services.AddSingleton<IEventPublisher>(sp => sp.GetRequiredService<RabbitEventPublisher>());
        services.AddSingleton<RabbitPartitionSubscriber>();
        services.AddSingleton<IPartitionSubscriber>(sp => sp.GetRequiredService<RabbitPartitionSubscriber>());
        return services;
    }
}
