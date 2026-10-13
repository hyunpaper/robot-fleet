using System.Text.Json;
using Microsoft.Extensions.Logging;
using RobotFleet.Shared.Messaging;
using RobotFleet.Shared.Pipeline;

namespace RobotFleet.Ingest;

/// 검증 → 멱등 → 발행 → 응답. 10 §4 · 08 §9
public sealed class IngestService
{
    private readonly IIdemStore? _idem;
    private readonly IEventPublisher _publisher;
    private readonly TimeProvider _time;
    private readonly ILogger<IngestService> _logger;

    /// idem이 null = Redis 미설정 → 멱등 1단계를 건너뛴다(fail-open, 10 §9). 대체 구현을 두지 않는다
    public IngestService(IEventPublisher publisher, TimeProvider time, ILogger<IngestService> logger, IIdemStore? idem = null)
    {
        _idem = idem;
        _publisher = publisher;
        _time = time;
        _logger = logger;
    }

    /// body = POST /api/v1/events/batch 요청 본문(UTF-8 JSON)
    public async Task<IngestResponse> HandleAsync(ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        // 1. 검증
        var (batch, messages, error) = Parse(body);
        if (batch is null || messages is null)
        {
            IngestMetrics.Batches.WithLabels("invalid").Inc();
            return Respond(400, new IngestResponseBody(null, 0, 0, false, error));
        }

        foreach (var e in batch.Events)
            IngestMetrics.EventsReceived.WithLabels(e.EventType).Inc();

        // 2. 멱등 1단계
        var begin = await TryBeginAsync(batch.BatchId, ct);
        switch (begin.Status)
        {
            case IdemBeginStatus.Done:
                IngestMetrics.Batches.WithLabels("duplicate").Inc();
                return Respond(202, new IngestResponseBody(batch.BatchId, batch.Events.Count, 0, true, null));
            case IdemBeginStatus.Busy:
                IngestMetrics.Batches.WithLabels("unavailable").Inc();
                return Respond(503, new IngestResponseBody(batch.BatchId, 0, 0, false, "다른 인스턴스가 발행 중"), retryAfter: 1);
            case IdemBeginStatus.Bypass:
                IngestMetrics.IdemBypass.Inc();
                break;
            case IdemBeginStatus.Takeover:
                IngestMetrics.IdemOrphanTakeover.Inc();
                break;
        }

        // 3 · 4. 발행 — task · conflict 확인만 성공 판정에 넣는다
        var started = _time.GetTimestamp();
        PublishOutcome outcome;
        try
        {
            outcome = await _publisher.PublishBatchAsync(messages, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "배치 {BatchId} 발행 실패", batch.BatchId);
            var reliable = messages.Count(m => m.Category != EventCategory.Telemetry);
            outcome = new PublishOutcome(0, reliable, 0, messages.Count - reliable, new Dictionary<string, int> { ["exception"] = 1 });
        }

        IngestMetrics.PublishSeconds.Observe(_time.GetElapsedTime(started).TotalSeconds);
        foreach (var (reason, count) in outcome.DropReasons)
            IngestMetrics.TelemetryDropped.WithLabels(reason).Inc(count);

        // 5. 응답
        if (outcome.ReliableFailed > 0)
        {
            if (_idem is not null && begin.Status is IdemBeginStatus.Begin or IdemBeginStatus.Takeover)
                await SafeAsync(() => _idem.AbortAsync(batch.BatchId, begin.PendingSinceMs, ct), "Abort");
            IngestMetrics.Batches.WithLabels("unavailable").Inc();
            return Respond(503, new IngestResponseBody(batch.BatchId, 0, outcome.TelemetryDropped, false,
                $"task · conflict {outcome.ReliableFailed}건 확인 실패"), retryAfter: 1);
        }

        if (_idem is not null)
            await SafeAsync(() => _idem.CompleteAsync(batch.BatchId, ct), "Complete");
        IngestMetrics.Batches.WithLabels("accepted").Inc();
        return Respond(202, new IngestResponseBody(batch.BatchId, batch.Events.Count, outcome.TelemetryDropped, false, null));
    }

    /// 400 사유는 첫 오류만. 10 §4 단계 1
    internal static (EventBatch? Batch, IReadOnlyList<OutgoingMessage>? Messages, string? Error) Parse(ReadOnlyMemory<byte> body)
    {
        EventBatch batch;
        try
        {
            batch = FleetJson.Deserialize<EventBatch>(body.Span);
        }
        catch (JsonException ex)
        {
            return (null, null, $"JSON 형식 오류: {ex.Message}");
        }

        if (batch.SchemaVersion != FleetSchema.Version)
            return (null, null, $"schemaVersion {batch.SchemaVersion}은(는) 지원하지 않는다");
        if (!IsUuidV7(batch.BatchId))
            return (null, null, "batchId가 UUIDv7이 아니다");
        if (string.IsNullOrWhiteSpace(batch.MapId))
            return (null, null, "mapId가 비어 있다");
        if (string.IsNullOrWhiteSpace(batch.RunId))
            return (null, null, "runId가 비어 있다");
        if (batch.TickMs < 1)
            return (null, null, "tickMs는 1 이상이어야 한다");
        if (batch.Events is null || batch.Events.Count is < 1 or > FleetSchema.MaxEventsPerBatch)
            return (null, null, $"events는 1~{FleetSchema.MaxEventsPerBatch}건이어야 한다");

        var messages = new List<OutgoingMessage>(batch.Events.Count);
        for (var i = 0; i < batch.Events.Count; i++)
        {
            var item = batch.Events[i];
            if (item?.EventType is null || !EventType.All.Contains(item.EventType))
                return (null, null, $"events[{i}]: 알 수 없는 eventType '{item?.EventType}'");
            if (item.Payload.ValueKind != JsonValueKind.Object)
                return (null, null, $"events[{i}]: payload가 객체가 아니다");

            string key;
            try
            {
                key = RoutingKeys.Of(item.EventType, item.Payload, batch.MapId);
            }
            catch (ArgumentException ex)
            {
                return (null, null, $"events[{i}]: {ex.Message}");
            }

            var envelope = EventEnvelope.From(batch, i);
            messages.Add(new OutgoingMessage(EventCategories.Of(item.EventType), key, envelope.EventId, item.EventType,
                EventCategories.IsPersistent(item.EventType), FleetJson.Serialize(envelope)));
        }

        return (batch, messages, null);
    }

    internal static bool IsUuidV7(string? id) =>
        id is { Length: 36 } && Guid.TryParseExact(id, "D", out _) && id[14] == '7' && id[19] is '8' or '9' or 'a' or 'b' or 'A' or 'B';

    private async Task<IdemBegin> TryBeginAsync(string batchId, CancellationToken ct)
    {
        if (_idem is null) return new IdemBegin(IdemBeginStatus.Bypass, 0);
        try
        {
            return await _idem.TryBeginAsync(batchId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "멱등 저장소 오류, 1단계 생략");
            return new IdemBegin(IdemBeginStatus.Bypass, 0);
        }
    }

    // Complete · Abort 실패는 응답을 바꾸지 않는다(멱등 키는 유실 허용, 07)
    private async Task SafeAsync(Func<Task> action, string what)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "멱등 키 {What} 실패(무시)", what);
        }
    }

    private static IngestResponse Respond(int status, IngestResponseBody body, int? retryAfter = null) =>
        new(status, body, retryAfter);
}
