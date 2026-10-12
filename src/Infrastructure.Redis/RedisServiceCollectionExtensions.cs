using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RobotFleet.Shared.Pipeline;

namespace RobotFleet.Infrastructure.Redis;

public static class RedisServiceCollectionExtensions
{
    /// ConnectionString이 있으면 RedisIdemStore · RedisPartitionRoster를 등록한다.
    /// 비면 아무것도 등록하지 않는다 — IngestService · AssignmentCoordinator가 null을 받아 멱등 1단계 생략 ·
    /// 단일 워커 경로로 간다(10 §9). 실행 중 Redis 오류는 RedisIdemStore가 Bypass로 돌려준다
    public static IServiceCollection AddFleetRedis(this IServiceCollection services, Action<RedisOptions> configure)
    {
        var options = new RedisOptions();
        configure(options);
        services.TryAddSingleton(TimeProvider.System);
        if (string.IsNullOrWhiteSpace(options.ConnectionString)) return services;

        services.AddSingleton(options);
        services.AddSingleton<RedisConnectionProvider>();
        services.AddSingleton<IIdemStore, RedisIdemStore>();
        services.AddSingleton<IPartitionRoster, RedisPartitionRoster>();
        return services;
    }
}
