using RobotFleet.Shared.Pipeline;

namespace RobotFleet.Infrastructure.InMemory;

/// PgEventStore와 같은 유니크 · RETURNING 집계 의미론. 10 §12
public sealed class InMemoryEventStore : IEventStore
{
    public Task<StoreResult> StoreBatchAsync(StoreBatch batch, CancellationToken ct) => throw new NotImplementedException();

    public Task<IReadOnlyDictionary<string, RobotStateRow>> GetRobotStatesAsync(string mapId,
        IReadOnlyCollection<string> robotIds, CancellationToken ct) => throw new NotImplementedException();

    public Task<IReadOnlySet<string>> GetExistingStatusEventIdsAsync(IReadOnlyCollection<string> eventIds, CancellationToken ct) =>
        throw new NotImplementedException();
}
