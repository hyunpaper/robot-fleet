using System.Text;
using FluentAssertions;
using RobotFleet.Shared.Messaging;
using Xunit;

namespace RobotFleet.Shared.Tests.Messaging;

public class EnvelopeJsonTests
{
    private const string BatchJson =
        """{"schemaVersion":1,"batchId":"018f3c2a-7b1e-7c3d-9a2f-0123456789ab","mapId":"factory-a","tick":142057,"tickMs":100,"occurredAt":"2026-04-01T09:14:23.412Z","events":[""" +
        """{"eventType":"ConflictDetected","payload":{"conflictType":"vertex","x":18,"y":6,"robotIds":["R-002","R-017"],"winnerRobotId":"R-017","resolution":"wait","waitMs":100}},""" +
        """{"eventType":"RobotStatusChanged","payload":{"robotId":"R-002","from":1,"to":2,"reason":"conflict_wait"}},""" +
        """{"eventType":"RobotTelemetry","payload":{"robotId":"R-001","x":3,"y":12,"heading":0,"status":1,"battery":91}},""" +
        """{"eventType":"RobotTelemetry","payload":{"robotId":"R-002","x":18,"y":7,"heading":2,"status":2,"battery":64}}]}""";

    [Fact] // M-01
    public void BatchExample_RoundTrips_ToSameBytes()
    {
        var input = Encoding.UTF8.GetBytes(BatchJson);

        var batch = FleetJson.Deserialize<EventBatch>(input);
        var output = FleetJson.Serialize(batch);

        batch.TickMs.Should().Be(100);
        batch.Events.Should().HaveCount(4);
        Encoding.UTF8.GetString(output).Should().Be(BatchJson);
    }

    [Fact] // M-01
    public void Timestamp_ParsesToSameInstant_AndNonUtcOffsetIsWrittenAsUtc()
    {
        var json = BatchJson.Replace("09:14:23.412Z", "18:14:23.412+09:00");

        var batch = FleetJson.Deserialize<EventBatch>(Encoding.UTF8.GetBytes(json));
        Encoding.UTF8.GetString(FleetJson.Serialize(batch)).Should().Be(BatchJson);

        batch.OccurredAt.Should().Be(new DateTimeOffset(2026, 4, 1, 9, 14, 23, 412, TimeSpan.Zero));
    }

    [Fact] // M-01
    public void Payload_Serializes_CamelCase_InDeclarationOrder_WithoutNulls()
    {
        var payload = new RobotTelemetryPayload("R-012", 23, 14, 1, 1, 87, null, 7, 19);

        var json = Encoding.UTF8.GetString(FleetJson.Serialize(payload));

        json.Should().Be("""{"robotId":"R-012","x":23,"y":14,"heading":1,"status":1,"battery":87,"pathIndex":7,"pathLength":19}""");
    }

    [Fact] // M-01
    public void NestedPayload_RoundTrips()
    {
        var payload = new TaskCreatedPayload("T-00042", 0, new CellDto(6, 1), new CellDto(52, 33), 0);

        var element = FleetJson.ToElement(payload);
        var envelope = new EventEnvelope(1, "b/0", EventType.TaskCreated, "factory-a", 1, 100,
            DateTimeOffset.UnixEpoch, element);

        FleetJson.PayloadOf<TaskCreatedPayload>(envelope).Should().Be(payload);
        element.GetRawText().Should().Be("""{"taskId":"T-00042","type":0,"origin":{"x":6,"y":1},"destination":{"x":52,"y":33},"priority":0}""");
    }

    [Fact] // M-03
    public void EnvelopeFrom_BuildsEventId_AndCopiesBatchFields()
    {
        var batch = FleetJson.Deserialize<EventBatch>(Encoding.UTF8.GetBytes(BatchJson));
        var many = batch with { Events = Enumerable.Range(0, 20).Select(_ => batch.Events[2]).ToList() };

        var envelope = EventEnvelope.From(many, 17);

        envelope.EventId.Should().Be($"{batch.BatchId}/17");
        envelope.TickMs.Should().Be(100);
        envelope.Tick.Should().Be(142057);
        envelope.MapId.Should().Be("factory-a");
        envelope.SchemaVersion.Should().Be(FleetSchema.Version);
        envelope.EventType.Should().Be(EventType.RobotTelemetry);
        envelope.OccurredAt.Should().Be(batch.OccurredAt);
    }

    [Fact] // M-03
    public void EventCategories_FollowMappingTable()
    {
        EventCategories.Of(EventType.RobotTelemetry).Should().Be(EventCategory.Telemetry);
        EventCategories.Of(EventType.RobotStatusChanged).Should().Be(EventCategory.Task);
        EventCategories.Of(EventType.PathReplanned).Should().Be(EventCategory.Conflict);
        EventCategories.IsPersistent(EventType.RobotTelemetry).Should().BeFalse();
        EventCategories.IsPersistent(EventType.PathReplanned).Should().BeFalse();
        EventCategories.IsPersistent(EventType.DeadlockDetected).Should().BeTrue();
        EventCategories.IsPersistent(EventType.RobotStatusChanged).Should().BeTrue();
        EventType.All.Should().HaveCount(10);
    }
}
