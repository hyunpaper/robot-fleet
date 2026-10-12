using System.Text.Json;
using Microsoft.Extensions.Logging;
using RobotFleet.Shared.Domain;
using RobotFleet.Shared.Messaging;
using RobotFleet.Shared.Pipeline;

namespace RobotFleet.EventWorker;

/// 배치 하나를 StoreBatch로 바꾸고(전이 검증 포함) 저장한다. 10 §6
public sealed class EventHandlers
{
    private readonly IEventStore _store;
    private readonly IEventPublisher _publisher;
    private readonly WorkerOptions _options;
    private readonly ILogger<EventHandlers> _logger;

    public EventHandlers(IEventStore store, IEventPublisher publisher, WorkerOptions options, ILogger<EventHandlers> logger)
    {
        _store = store;
        _publisher = publisher;
        _options = options;
        _logger = logger;
    }

    /// 반환: 처리 결과(독 메시지 tag 목록 포함). 저장소 · 발행 오류는 예외로 던진다(호출자가 같은 배치를 재시도)
    public async Task<BatchOutcome> HandleAsync(IReadOnlyList<Delivery> batch, CancellationToken ct)
    {
        var poison = new List<ulong>();
        var telemetry = new List<TelemetryRow>();
        var tasks = new List<TaskEventRow>();
        var conflicts = new List<ConflictRow>();
        var statuses = new List<(EventEnvelope Envelope, RobotStatusChangedPayload Payload, Delivery Delivery)>();
        var skipped = 0;

        foreach (var d in batch)
        {
            try
            {
                var env = FleetJson.Deserialize<EventEnvelope>(d.Body.Span);
                if (env.SchemaVersion != FleetSchema.Version || !EventType.All.Contains(env.EventType))
                    throw new JsonException($"지원하지 않는 메시지: v{env.SchemaVersion} {env.EventType}");
                switch (env.EventType)
                {
                    case EventType.RobotTelemetry:
                        var t = FleetJson.PayloadOf<RobotTelemetryPayload>(env);
                        telemetry.Add(new TelemetryRow(env.MapId, t.RobotId, env.Tick, (short)t.X, (short)t.Y, (short)t.Heading,
                            (short)t.Status, (short)t.Battery, env.OccurredAt, env.TickMs));
                        break;
                    case EventType.RobotStatusChanged:
                        statuses.Add((env, FleetJson.PayloadOf<RobotStatusChangedPayload>(env), d));
                        break;
                    case EventType.ConflictDetected or EventType.DeadlockDetected:
                        var row = new ConflictRow(env.EventId, env.MapId, env.Tick, env.OccurredAt, env.EventType, env.Payload);
                        _ = StoreMapping.ToConflict(row);   // 해석 실패는 독 메시지
                        conflicts.Add(row);
                        break;
                    case EventType.PathReplanned:
                        skipped++;   // M3는 세기만 (10 §6)
                        break;
                    default:
                        var taskRow = new TaskEventRow(env.EventId, env.MapId, RoutingKeys.Of(env.EventType, env.Payload, env.MapId),
                            env.EventType, env.Tick, env.OccurredAt, env.Payload);
                        _ = StoreMapping.ToHistory(taskRow);
                        tasks.Add(taskRow);
                        break;
                }
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or NotSupportedException)
            {
                _logger.LogWarning(ex, "독 메시지 {MessageId} ({Queue})", d.MessageId, d.Queue);
                poison.Add(d.DeliveryTag);
            }
        }

        var (statusRows, invalid, duplicateStatus) = await ValidateAsync(statuses, ct);
        var result = await _store.StoreBatchAsync(new StoreBatch(telemetry, tasks, statusRows, conflicts), ct);

        // 위반 통지는 커밋 뒤에. 실패하면 예외 → 같은 배치 재시도(영수증은 이미 있어 중복으로 걸러진다)
        foreach (var (env, delivery) in invalid)
        {
            await _publisher.PublishToQueueAsync(TopologySpec.InvalidQueueName(_options.TopologyPrefix),
                new OutgoingMessage(EventCategory.Task, delivery.Queue, env.EventId, env.EventType, true, delivery.Body), ct);
        }

        var stored = result.TelemetryInserted + result.TaskInserted + result.StatusInserted + result.ConflictInserted;
        var duplicate = result.TelemetryDuplicate + result.TaskDuplicate + result.StatusDuplicate + result.ConflictDuplicate
                        + duplicateStatus;
        WorkerMetrics.Record(batch, result, invalid.Count, poison.Count, skipped);
        return new BatchOutcome(poison, stored, duplicate, invalid.Count);
    }

    // 03 전이 표 + 직전 상태 일치. 이미 영수증이 있는 이벤트(재전달)는 검증하지 않는다
    private async Task<(List<StatusChangeRow> Rows, List<(EventEnvelope, Delivery)> Invalid, int Duplicate)> ValidateAsync(
        List<(EventEnvelope Envelope, RobotStatusChangedPayload Payload, Delivery Delivery)> statuses, CancellationToken ct)
    {
        var rows = new List<StatusChangeRow>();
        var invalid = new List<(EventEnvelope, Delivery)>();
        if (statuses.Count == 0) return (rows, invalid, 0);

        var existing = await _store.GetExistingStatusEventIdsAsync(statuses.Select(s => s.Envelope.EventId).ToList(), ct);
        var current = new Dictionary<(string Map, string Robot), short?>();
        foreach (var group in statuses.GroupBy(s => s.Envelope.MapId))
        {
            var states = await _store.GetRobotStatesAsync(group.Key, group.Select(s => s.Payload.RobotId).Distinct().ToList(), ct);
            foreach (var s in group)
                current[(group.Key, s.Payload.RobotId)] = states.TryGetValue(s.Payload.RobotId, out var st) ? st.Status : null;
        }

        var duplicate = 0;
        foreach (var (env, p, d) in statuses)
        {
            if (existing.Contains(env.EventId))
            {
                duplicate++;
                continue;
            }

            var key = (env.MapId, p.RobotId);
            var from = (short)p.From;
            var valid = StatusTransitions.IsAllowed((RobotStatus)p.From, (RobotStatus)p.To, p.Reason)
                        && (current[key] is null || current[key] == from);
            if (valid) current[key] = (short)p.To;
            else invalid.Add((env, d));
            rows.Add(new StatusChangeRow(env.EventId, env.MapId, p.RobotId, env.Tick, env.OccurredAt, from, (short)p.To, p.Reason, valid));
        }

        return (rows, invalid, duplicate);
    }
}

/// Poison = 역직렬화 실패 · 모르는 schemaVersion → Reject(DLX)
public sealed record BatchOutcome(IReadOnlyList<ulong> Poison, int Stored, int Duplicate, int Invalid);
