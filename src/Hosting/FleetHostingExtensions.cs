using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RobotFleet.EventWorker;
using RobotFleet.Infrastructure.Postgres;
using RobotFleet.Infrastructure.RabbitMq;
using RobotFleet.Infrastructure.Redis;
using RobotFleet.Ingest;

namespace RobotFleet.Hosting;

public static class FleetHostingExtensions
{
    public static IServiceCollection AddFleetIngest(this IServiceCollection services, IConfiguration config)
    {
        services.AddFleetRabbitMq(o =>
        {
            o.ConnectionString = config.GetConnection("RabbitMq") ?? "";
            o.TopologyPrefix = config["Fleet:Topology:Prefix"] ?? "";
        });
        services.AddFleetRedis(o =>
        {
            o.ConnectionString = config.GetConnection("Redis") ?? "";
            o.KeyPrefix = config["Fleet:Redis:KeyPrefix"] ?? "";
        });
        services.AddIngestCore();
        return services;
    }

    /// 등록 순서가 기동 순서다
    public static IServiceCollection AddFleetWorker(this IServiceCollection services, IConfiguration config)
    {
        services.AddFleetPostgres(o => o.ConnectionString = config.GetConnection("Postgres") ?? "");
        services.AddFleetRabbitMq(o =>
        {
            o.ConnectionString = config.GetConnection("RabbitMq") ?? "";
            o.TopologyPrefix = config["Fleet:Topology:Prefix"] ?? "";
        });
        services.AddFleetRedis(o =>
        {
            o.ConnectionString = config.GetConnection("Redis") ?? "";
            o.KeyPrefix = config["Fleet:Redis:KeyPrefix"] ?? "";
        });
        services.AddWorkerCore(o =>
        {
            var id = config["Fleet:Worker:Id"];
            if (!string.IsNullOrWhiteSpace(id)) o.WorkerId = id;
            o.TopologyPrefix = config["Fleet:Topology:Prefix"] ?? "";
        });
        services.AddHostedService<PartitionMaintenanceService>();
        return services;
    }
}
