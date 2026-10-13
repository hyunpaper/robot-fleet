using System.Text.Json;
using RobotFleet.Shared.Domain;
using RobotFleet.Shared.Messaging;
using TaskStatus = RobotFleet.Shared.Domain.TaskStatus;

namespace RobotFleet.Shared.Pipeline;

/// task_history 한 줄에 반영할 값. null = 기존 값 유지(COALESCE). 10 §6
public sealed record TaskHistoryChange(
    string RunId, string TaskId, string MapId, short Status, string? RobotId, short? TaskType,
    short? OriginX, short? OriginY, short? DestX, short? DestY,
    DateTimeOffset? CreatedAt, DateTimeOffset? AssignedAt, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt,
    int? PathLength, int? ActualSteps, int? WaitMs);

/// conflict_log 한 줄. 코드 값은 01 주석(vertex / edge / following / deadlock, wait / reroute / yield / failed).
/// WaitMs는 ConflictDetected, ResolvedMs · Yield*는 DeadlockDetected
public sealed record ConflictLogChange(
    string EventId, string RunId, string MapId, long Tick, short ConflictType, short? X, short? Y,
    IReadOnlyList<string> RobotIds, short? Resolution, int? WaitMs, int? ResolvedMs,
    string? YieldRobotId, short? YieldToX, short? YieldToY, short? SearchedDistance, int? NoYieldRetries,
    DateTimeOffset OccurredAt);

/// replan_log 한 줄. reason 코드는 01 주석(blocked / deadlock / map_changed)
public sealed record ReplanChange(
    string EventId, string RunId, string MapId, string RobotId, string? TaskId, long Tick, short Reason,
    int OldLength, int NewLength, double ComputeMs, DateTimeOffset OccurredAt);

public enum MetricsBucket { Move, Work, Wait, Idle }

/// 워커 핸들러(해석 검증)와 PgEventStore가 같은 규칙으로 행을 만든다
public static class StoreMapping
{
    public static short TaskStatusOf(string eventType) => eventType switch
    {
        EventType.TaskCreated => (short)TaskStatus.Pending,
        EventType.TaskAssigned => (short)TaskStatus.Assigned,
        EventType.TaskStarted => (short)TaskStatus.InProgress,
        EventType.TaskCompleted => (short)TaskStatus.Completed,
        EventType.TaskFailed => (short)TaskStatus.Failed,
        _ => throw new ArgumentException($"작업 이벤트가 아니다: {eventType}", nameof(eventType)),
    };

    public static TaskHistoryChange ToHistory(TaskEventRow row)
    {
        var status = TaskStatusOf(row.EventType);
        var at = row.OccurredAt;
        var empty = Empty(row, status);
        switch (row.EventType)
        {
            case EventType.TaskCreated:
                return Created(row, Read<TaskCreatedPayload>(row.Payload), status);
            case EventType.TaskAssigned:
                var a = Read<TaskAssignedPayload>(row.Payload);
                return empty with { RobotId = a.RobotId, AssignedAt = at, PathLength = a.PlannedPathLength };
            case EventType.TaskStarted:
                return empty with { RobotId = Read<TaskStartedPayload>(row.Payload).RobotId, StartedAt = at };
            case EventType.TaskCompleted:
                var c = Read<TaskCompletedPayload>(row.Payload);
                return empty with { RobotId = c.RobotId, CompletedAt = at, ActualSteps = c.ActualSteps, WaitMs = c.WaitMs };
            default:
                return empty with { RobotId = Read<TaskFailedPayload>(row.Payload).RobotId, CompletedAt = at };
        }
    }

    /// TaskCompleted만 task_count에 더한다. 그 밖은 null
    public static string? CompletedRobotOf(TaskEventRow row) =>
        row.EventType == EventType.TaskCompleted ? Read<TaskCompletedPayload>(row.Payload).RobotId : null;

    public static ConflictLogChange ToConflict(ConflictRow row)
    {
        if (row.EventType == EventType.DeadlockDetected)
        {
            var d = Read<DeadlockDetectedPayload>(row.Payload);
            var first = d.Cells.Count > 0 ? d.Cells[0] : null;
            // 양보가 있을 때만 yield_robot_id · yield_to_x/y를 싣는다. 양보 없는 교착(resolution=failed 등)은
            // YieldToX/Y가 0으로 와도 좌표가 아니라 "없음"이므로 NULL로 저장한다(안 그러면 (0,0)으로 잘못 남는다)
            var hasYield = !string.IsNullOrEmpty(d.YieldRobotId);
            return new ConflictLogChange(row.EventId, row.RunId, row.MapId, row.Tick, 3, (short?)first?.X, (short?)first?.Y,
                d.Cycle, ResolutionCode(d.Resolution), null, d.ResolvedMs,
                hasYield ? d.YieldRobotId : null, hasYield ? (short)d.YieldToX : null, hasYield ? (short)d.YieldToY : null,
                (short)d.SearchedDistance, d.NoYieldRetries, row.OccurredAt);
        }

        var c = Read<ConflictDetectedPayload>(row.Payload);
        return new ConflictLogChange(row.EventId, row.RunId, row.MapId, row.Tick, ConflictTypeCode(c.ConflictType), (short)c.X,
            (short)c.Y, c.RobotIds, ResolutionCode(c.Resolution), c.WaitMs, null, null, null, null, null, null, row.OccurredAt);
    }

    public static ReplanChange ToReplan(ReplanRow row)
    {
        var p = Read<PathReplannedPayload>(row.Payload);
        return new ReplanChange(row.EventId, row.RunId, row.MapId, p.RobotId, p.TaskId, row.Tick, ReplanReasonCode(p.Reason),
            p.OldLength, p.NewLength, p.ComputeMs, row.OccurredAt);
    }

    /// SimulationStarted · SimulationEnded 페이로드 해석. 실패하면 예외(독 메시지)
    public static object ToRunEvent(RunEventRow row) => row.EventType switch
    {
        EventType.SimulationStarted => Read<SimulationStartedPayload>(row.Payload),
        EventType.SimulationEnded => Read<SimulationEndedPayload>(row.Payload),
        _ => throw new ArgumentException($"run 이벤트가 아니다: {row.EventType}", nameof(row)),
    };

    /// 상태별 시간 분해(05). Charging · Error는 어느 칸에도 넣지 않는다
    public static MetricsBucket? BucketOf(short status) => (RobotStatus)status switch
    {
        RobotStatus.Moving => MetricsBucket.Move,
        RobotStatus.Loading or RobotStatus.Unloading => MetricsBucket.Work,
        RobotStatus.Waiting => MetricsBucket.Wait,
        RobotStatus.Idle => MetricsBucket.Idle,
        _ => null,
    };

    public static DateTimeOffset MinuteOf(DateTimeOffset t)
    {
        var u = t.ToUniversalTime();
        return new DateTimeOffset(u.Year, u.Month, u.Day, u.Hour, u.Minute, 0, TimeSpan.Zero);
    }

    public static short ConflictTypeCode(string type) => type switch
    {
        "vertex" => 0,
        "edge" => 1,
        "following" => 2,
        "deadlock" => 3,
        _ => throw new ArgumentException($"알 수 없는 충돌 유형: {type}", nameof(type)),
    };

    public static short? ResolutionCode(string? resolution) => resolution switch
    {
        "wait" => 0,
        "reroute" => 1,
        "yield" => 2,
        "failed" => 3,
        null => null,
        _ => throw new ArgumentException($"알 수 없는 해소 방식: {resolution}", nameof(resolution)),
    };

    public static short ReplanReasonCode(string reason) => reason switch
    {
        "blocked" => 0,
        "deadlock" => 1,
        "map_changed" => 2,
        _ => throw new ArgumentException($"알 수 없는 재계획 사유: {reason}", nameof(reason)),
    };

    public static T Read<T>(JsonElement payload) =>
        payload.Deserialize<T>(FleetJson.Options) ?? throw new JsonException($"{typeof(T).Name} 페이로드가 null이다");

    private static TaskHistoryChange Created(TaskEventRow row, TaskCreatedPayload p, short status) =>
        Empty(row, status) with
        {
            TaskType = (short)p.Type,
            OriginX = (short)p.Origin.X,
            OriginY = (short)p.Origin.Y,
            DestX = (short)p.Destination.X,
            DestY = (short)p.Destination.Y,
            CreatedAt = row.OccurredAt,
        };

    private static TaskHistoryChange Empty(TaskEventRow row, short status) =>
        new(row.RunId, row.TaskId, row.MapId, status, null, null, null, null, null, null, null, null, null, null, null, null, null);
}
