using RobotFleet.Shared.Messaging;

namespace RobotFleet.Infrastructure.Tests.Pipeline;

/// 시험마다 실제 인프라 시험대를 만들고 지운다
public abstract class PipelineTestBase : IAsyncLifetime
{
    protected const string Map = "m-it";
    private static readonly UuidV7 Ids = new();
    private PipelineRig? _rig;

    internal PipelineRig Rig => _rig ?? throw new InvalidOperationException("RigAsync를 먼저 부른다");

    internal async Task<PipelineRig> RigAsync(bool redis, bool postgres) =>
        _rig = await PipelineRig.CreateAsync(redis, postgres);

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _rig?.DisposeAsync().AsTask() ?? Task.CompletedTask;

    protected static EventItem Item<T>(string type, T payload) => new(type, FleetJson.ToElement(payload));

    protected static EventItem Telemetry(string robot, int status = 1) =>
        Item(EventType.RobotTelemetry, new RobotTelemetryPayload(robot, 1, 2, 0, status, 90, null, 0, 0));

    protected static EventItem Created(string task) =>
        Item(EventType.TaskCreated, new TaskCreatedPayload(task, 0, new CellDto(1, 1), new CellDto(2, 2), 0));

    protected static EventItem Status(string robot, int from, int to, string reason) =>
        Item(EventType.RobotStatusChanged, new RobotStatusChangedPayload(robot, from, to, reason));

    protected const string Run = "20261013-143022-r200";

    protected static EventBatch Batch(long tick, params EventItem[] events) => BatchOf(Run, tick, events);

    protected static EventBatch BatchOf(string runId, long tick, params EventItem[] events) =>
        new(FleetSchema.Version, Ids.Next(), Map, runId, tick, 100, DateTimeOffset.UtcNow, events);
}
