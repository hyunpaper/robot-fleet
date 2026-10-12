using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RobotFleet.Infrastructure.InMemory;
using RobotFleet.Shared.Pipeline;

namespace RobotFleet.Infrastructure.Redis;

public static class RedisServiceCollectionExtensions
{
    /// ConnectionString이 있으면 RedisIdemStore · RedisPartitionRoster,
    /// 비면 BypassIdemStore(항상 Bypass) · InMemoryPartitionRoster(configured: false). 10 §9
    public static IServiceCollection AddFleetRedis(this IServiceCollection services, Action<RedisOptions> configure)
    {
        var options = new RedisOptions();
        configure(options);
        services.TryAddSingleton(TimeProvider.System);
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            services.AddSingleton<IIdemStore, BypassIdemStore>();
            services.AddSingleton<IPartitionRoster>(sp =>
                new InMemoryPartitionRoster(sp.GetRequiredService<TimeProvider>(), configured: false));
            return services;
        }

        services.AddSingleton(options);
        services.AddSingleton<RedisConnectionProvider>();
        services.AddSingleton<IIdemStore, RedisIdemStore>();
        services.AddSingleton<IPartitionRoster, RedisPartitionRoster>();
        return services;
    }
}

/// Redis 미설정 — 매 배치 멱등 1단계 생략(fail-open)
public sealed class BypassIdemStore : IIdemStore
{
    public Task<IdemBegin> TryBeginAsync(string batchId, CancellationToken ct) =>
        Task.FromResult(new IdemBegin(IdemBeginStatus.Bypass, 0));

    public Task CompleteAsync(string batchId, CancellationToken ct) => Task.CompletedTask;

    public Task AbortAsync(string batchId, long pendingSinceMs, CancellationToken ct) => Task.CompletedTask;
}
