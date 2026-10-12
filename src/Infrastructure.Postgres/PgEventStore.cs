using Microsoft.Extensions.Logging;
using Npgsql;
using RobotFleet.Shared.Pipeline;

namespace RobotFleet.Infrastructure.Postgres;

/// unnest 배치 INSERT · ON CONFLICT DO NOTHING RETURNING으로 새 행만 집계. 트랜잭션 1개. 10 §6 · §8
public sealed class PgEventStore : IEventStore
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<PgEventStore> _logger;

    public PgEventStore(NpgsqlDataSource dataSource, ILogger<PgEventStore> logger)
    {
        _dataSource = dataSource;
        _logger = logger;
    }

    public Task<StoreResult> StoreBatchAsync(StoreBatch batch, CancellationToken ct) =>
        throw new NotImplementedException($"{_dataSource}{_logger}");

    public Task<IReadOnlyDictionary<string, RobotStateRow>> GetRobotStatesAsync(string mapId,
        IReadOnlyCollection<string> robotIds, CancellationToken ct) => throw new NotImplementedException();

    public Task<IReadOnlySet<string>> GetExistingStatusEventIdsAsync(IReadOnlyCollection<string> eventIds, CancellationToken ct) =>
        throw new NotImplementedException();
}
