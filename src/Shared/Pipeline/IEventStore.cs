using System.Text.Json;

namespace RobotFleet.Shared.Pipeline;

// 모든 행은 봉투의 runId · mapId를 그대로 싣는다. DB 컬럼은 run_id · map_code(01 키 정책)

public sealed record TelemetryRow(string RunId, string MapId, string RobotId, long Tick, short X, short Y, short Heading,
    short Status, short? Battery, DateTimeOffset OccurredAt, int TickMs);

public sealed record TaskEventRow(string EventId, string RunId, string MapId, string TaskId, string EventType, long Tick,
    DateTimeOffset OccurredAt, JsonElement Payload);

public sealed record StatusChangeRow(string EventId, string RunId, string MapId, string RobotId, long Tick,
    DateTimeOffset OccurredAt, short From, short To, string Reason, bool Valid);

public sealed record ConflictRow(string EventId, string RunId, string MapId, long Tick, DateTimeOffset OccurredAt,
    string EventType, JsonElement Payload);

/// PathReplanned → replan_log
public sealed record ReplanRow(string EventId, string RunId, string MapId, long Tick, DateTimeOffset OccurredAt,
    JsonElement Payload);

/// SimulationStarted · SimulationEnded → simulations
public sealed record RunEventRow(string EventId, string RunId, string MapId, string EventType, DateTimeOffset OccurredAt,
    JsonElement Payload);

public sealed record StoreBatch(IReadOnlyList<TelemetryRow> Telemetry, IReadOnlyList<TaskEventRow> TaskEvents,
    IReadOnlyList<StatusChangeRow> StatusChanges, IReadOnlyList<ConflictRow> Conflicts,
    IReadOnlyList<ReplanRow> Replans, IReadOnlyList<RunEventRow> RunEvents);

/// RunEvents = simulations UPSERT 건수(멱등이라 중복 구분 없음)
public sealed record StoreResult(int TelemetryInserted, int TelemetryDuplicate, int TaskInserted, int TaskDuplicate,
    int StatusInserted, int StatusDuplicate, int ConflictInserted, int ConflictDuplicate,
    int ReplanInserted, int ReplanDuplicate, int RunEvents);

public sealed record RobotStateRow(string RunId, string MapId, string RobotId, short Status, long LastTick, string LastEventId);

public interface IEventStore
{
    /// 트랜잭션 1개. 배치에 나온 run마다 simulations 최소 행을 먼저 만든다(FK 없음, 10 §6)
    Task<StoreResult> StoreBatchAsync(StoreBatch batch, CancellationToken ct);

    /// robot_state 조회 키 = (run_id, robot_id). run이 바뀌면 이전 run의 상태를 보지 않는다
    Task<IReadOnlyDictionary<string, RobotStateRow>> GetRobotStatesAsync(string runId,
        IReadOnlyCollection<string> robotIds, CancellationToken ct);

    Task<IReadOnlySet<string>> GetExistingStatusEventIdsAsync(IReadOnlyCollection<string> eventIds, CancellationToken ct);
}
