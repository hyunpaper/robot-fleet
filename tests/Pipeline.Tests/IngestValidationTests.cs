using System.Text.Json;
using RobotFleet.Ingest;
using RobotFleet.Shared.Messaging;

namespace RobotFleet.Pipeline.Tests;

/// 인프라 없는 순수 로직: I-16 검증 규칙과 메시지 생성(10 §4 단계 1 · 3), UUIDv7
public sealed class IngestValidationTests
{
    private static readonly UuidV7 Ids = new();

    private static EventItem Telemetry(string robot) => new(EventType.RobotTelemetry,
        FleetJson.ToElement(new RobotTelemetryPayload(robot, 1, 2, 0, 1, 90, null, 0, 0)));

    private static EventBatch Batch(params EventItem[] events) =>
        new(FleetSchema.Version, Ids.Next(), "m-test", "r-test", 7, 100, DateTimeOffset.Parse("2026-10-12T09:00:00Z"), events);

    public static TheoryData<string, Func<EventBatch, byte[]>> Invalids => new()
    {
        { "schemaVersion", b => FleetJson.Serialize(b with { SchemaVersion = 2 }) },
        { "UUIDv7", b => FleetJson.Serialize(b with { BatchId = Guid.NewGuid().ToString() }) },
        { "1~1000", b => FleetJson.Serialize(b with { Events = [] }) },
        { "1~1000", b => FleetJson.Serialize(b with { Events = Enumerable.Repeat(Telemetry("R-1"), 1001).ToList() }) },
        { "eventType", b => FleetJson.Serialize(b with { Events = [new EventItem("Teleport", JsonDocument.Parse("{}").RootElement)] }) },
        { "robotId", b => FleetJson.Serialize(b with { Events = [new EventItem(EventType.RobotTelemetry, JsonDocument.Parse("""{"x":1}""").RootElement)] }) },
        { "tickMs", b => FleetJson.Serialize(b with { TickMs = 0 }) },
        { "mapId", b => FleetJson.Serialize(b with { MapId = " " }) },
        { "runId", b => FleetJson.Serialize(b with { RunId = "" }) },
        { "JSON", _ => "{not json"u8.ToArray() },
    };

    [Theory]
    [MemberData(nameof(Invalids))]
    public void I16_검증_실패는_사유와_함께_거부(string reason, Func<EventBatch, byte[]> body)
    {
        var (batch, messages, error) = IngestService.Parse(body(Batch(Telemetry("R-1"))));
        batch.Should().BeNull();
        messages.Should().BeNull();
        error.Should().Contain(reason);
    }

    [Fact]
    public void 메시지는_배열_순서_eventId_라우팅_키_범주_지속성_tickMs_복사()
    {
        var created = new EventItem(EventType.TaskCreated,
            FleetJson.ToElement(new TaskCreatedPayload("T-1", 0, new CellDto(1, 1), new CellDto(2, 2), 0)));
        var batch = Batch(created, Telemetry("R-1"));
        var (_, messages, error) = IngestService.Parse(FleetJson.Serialize(batch));

        error.Should().BeNull();
        messages!.Select(m => (m.MessageId, m.RoutingKey, m.Category, m.Persistent)).Should().Equal(
            ($"{batch.BatchId}/0", "T-1", EventCategory.Task, true),
            ($"{batch.BatchId}/1", "R-1", EventCategory.Telemetry, false));
        FleetJson.Deserialize<EventEnvelope>(messages![0].Body.Span).TickMs.Should().Be(100);
    }

    [Fact]
    public void UUIDv7은_버전_7이고_같은_생성기에서_증가한다()
    {
        var ids = Enumerable.Range(0, 2000).Select(_ => Ids.Next()).ToList();
        ids.Should().OnlyContain(id => IngestService.IsUuidV7(id));
        ids.Should().BeInAscendingOrder(StringComparer.Ordinal).And.OnlyHaveUniqueItems();
    }
}
