using System.Text.Json;

namespace RobotFleet.Shared.Messaging;

public sealed record EventBatch(
    int SchemaVersion, string BatchId, string MapId, string RunId, long Tick, int TickMs,
    DateTimeOffset OccurredAt, IReadOnlyList<EventItem> Events);

public sealed record EventItem(string EventType, JsonElement Payload);

/// 큐 메시지 본문. eventId = "{batchId}/{index}"
public sealed record EventEnvelope(
    int SchemaVersion, string EventId, string EventType, string MapId, string RunId, long Tick, int TickMs,
    DateTimeOffset OccurredAt, JsonElement Payload)
{
    public static EventEnvelope From(EventBatch batch, int index)
    {
        var item = batch.Events[index];
        return new EventEnvelope(batch.SchemaVersion, $"{batch.BatchId}/{index}", item.EventType, batch.MapId,
            batch.RunId, batch.Tick, batch.TickMs, batch.OccurredAt, item.Payload);
    }
}
