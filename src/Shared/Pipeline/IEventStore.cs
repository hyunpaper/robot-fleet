using System.Text.Json;

namespace RobotFleet.Shared.Pipeline;

public sealed record TelemetryRow(string MapId, string RobotId, long Tick, short X, short Y, short Heading,
    short Status, short? Battery, DateTimeOffset OccurredAt, int TickMs);

public sealed record TaskEventRow(string EventId, string MapId, string TaskId, string EventType, long Tick,
    DateTimeOffset OccurredAt, JsonElement Payload);

public sealed record StatusChangeRow(string EventId, string MapId, string RobotId, long Tick,
    DateTimeOffset OccurredAt, short From, short To, string Reason, bool Valid);

public sealed record ConflictRow(string EventId, string MapId, long Tick, DateTimeOffset OccurredAt,
    string EventType, JsonElement Payload);

public sealed record StoreBatch(IReadOnlyList<TelemetryRow> Telemetry, IReadOnlyList<TaskEventRow> TaskEvents,
    IReadOnlyList<StatusChangeRow> StatusChanges, IReadOnlyList<ConflictRow> Conflicts);

public sealed record StoreResult(int TelemetryInserted, int TelemetryDuplicate, int TaskInserted, int TaskDuplicate,
    int StatusInserted, int StatusDuplicate, int ConflictInserted, int ConflictDuplicate);

public sealed record RobotStateRow(string MapId, string RobotId, short Status, long LastTick, string LastEventId);

public interface IEventStore
{
    /// 트랜잭션 1개
    Task<StoreResult> StoreBatchAsync(StoreBatch batch, CancellationToken ct);

    Task<IReadOnlyDictionary<string, RobotStateRow>> GetRobotStatesAsync(string mapId,
        IReadOnlyCollection<string> robotIds, CancellationToken ct);

    Task<IReadOnlySet<string>> GetExistingStatusEventIdsAsync(IReadOnlyCollection<string> eventIds, CancellationToken ct);
}
