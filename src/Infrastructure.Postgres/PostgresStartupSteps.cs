using RobotFleet.Infrastructure.Postgres.Migrations;
using RobotFleet.Shared.Pipeline;

namespace RobotFleet.Infrastructure.Postgres;

/// 마이그레이션 적용 → UTC 당일 · 익일 파티션. EventWorker 기동 단계 1 (10 §12)
public sealed class PostgresStartupStep : IStartupStep
{
    private readonly Migrator _migrator;
    private readonly PartitionMaintainer _partitions;

    public PostgresStartupStep(Migrator migrator, PartitionMaintainer partitions)
    {
        _migrator = migrator;
        _partitions = partitions;
    }

    public string Name => ReadinessComponents.PostgresMigrations;

    public async Task RunAsync(CancellationToken ct)
    {
        await _migrator.ApplyAsync(ct);
        await _partitions.EnsureAsync(ct);
    }
}
