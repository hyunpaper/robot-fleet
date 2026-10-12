using RobotFleet.Shared.Messaging;

namespace RobotFleet.Pipeline.Tests;

internal static class TestBatches
{
    public const string Map = "m-test";
    private static readonly UuidV7 Ids = new();

    public static string NewBatchId() => Ids.Next();

    public static EventItem Item<T>(string type, T payload) => new(type, FleetJson.ToElement(payload));

    public static EventItem Telemetry(string robot, int status = 1) =>
        Item(EventType.RobotTelemetry, new RobotTelemetryPayload(robot, 1, 2, 0, status, 90, null, 0, 0));

    public static EventItem Created(string task) =>
        Item(EventType.TaskCreated, new TaskCreatedPayload(task, 0, new CellDto(1, 1), new CellDto(2, 2), 0));

    public static EventItem Status(string robot, int from, int to, string reason) =>
        Item(EventType.RobotStatusChanged, new RobotStatusChangedPayload(robot, from, to, reason));

    public static EventItem Conflict() =>
        Item(EventType.ConflictDetected, new ConflictDetectedPayload("vertex", 3, 4, ["R-1", "R-2"], "R-2", "wait", 300));

    public static EventBatch Batch(long tick, params EventItem[] events) =>
        new(FleetSchema.Version, NewBatchId(), Map, tick, 100, DateTimeOffset.Parse("2026-10-12T09:00:00Z").AddMilliseconds(tick * 100), events);

    public static byte[] Body(EventBatch batch) => FleetJson.Serialize(batch);
}
