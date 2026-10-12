using FluentAssertions;
using RobotFleet.Shared.Messaging;
using RobotFleet.Shared.Pipeline;

namespace RobotFleet.TestSupport.Contracts;

/// IEventStore 계약 — W-10~W-12 · 중복 · RETURNING 집계(C-03). InMemory(L1) · Postgres(L2). 10 §6 · §12
public abstract class EventStoreContract
{
    protected const string Map = "m-contract";
    protected static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-10-12T09:14:23.412Z");

    protected abstract Task<IEventStore> CreateAsync();

    /// metrics_minute (move_ms, wait_ms, task_count) 의 해당 분
    protected abstract Task<(long MoveMs, long WaitMs, long TaskCount)> ReadMetricsAsync(string robotId, DateTimeOffset minute);

    /// task_history.status · robot_id, 없으면 null
    protected abstract Task<(short Status, string? RobotId)?> ReadTaskAsync(string taskId);

    protected async Task Duplicate_telemetry_is_counted_once_in_metrics()   // W-10
    {
        var store = await CreateAsync();
        var row = Telemetry("r-1", tick: 10, status: 1);
        var first = await store.StoreBatchAsync(Batch(telemetry: [row, Telemetry("r-1", 11, 2)]), default);
        var again = await store.StoreBatchAsync(Batch(telemetry: [row]), default);

        first.TelemetryInserted.Should().Be(2);
        again.Should().Be(new StoreResult(0, 1, 0, 0, 0, 0, 0, 0));
        (await ReadMetricsAsync("r-1", At)).Should().Be((100L, 100L, 0L));
    }

    protected async Task Duplicate_task_completed_counts_once()   // W-11
    {
        var store = await CreateAsync();
        var created = TaskEvent("e/0", "T-1", EventType.TaskCreated, new TaskCreatedPayload("T-1", 0, new CellDto(1, 2), new CellDto(3, 4), 0));
        var done = TaskEvent("e/1", "T-1", EventType.TaskCompleted, new TaskCompletedPayload("T-1", "r-1", 7, 7000, 300, 4000, 0));
        await store.StoreBatchAsync(Batch(tasks: [created, done]), default);
        var again = await store.StoreBatchAsync(Batch(tasks: [done]), default);

        again.TaskDuplicate.Should().Be(1);
        (await ReadMetricsAsync("r-1", At)).TaskCount.Should().Be(1);
        (await ReadTaskAsync("T-1")).Should().Be(((short)3, "r-1"));
    }

    protected async Task Task_history_is_monotonic()   // W-12
    {
        var store = await CreateAsync();
        await store.StoreBatchAsync(Batch(tasks:
        [
            TaskEvent("h/0", "T-2", EventType.TaskCreated, new TaskCreatedPayload("T-2", 0, new CellDto(1, 2), new CellDto(3, 4), 0)),
            TaskEvent("h/1", "T-2", EventType.TaskStarted, new TaskStartedPayload("T-2", "r-2")),
        ]), default);
        await store.StoreBatchAsync(Batch(tasks: [TaskEvent("h/2", "T-2", EventType.TaskAssigned, new TaskAssignedPayload("T-2", "r-9", 5, 900))]), default);

        (await ReadTaskAsync("T-2")).Should().Be(((short)2, "r-2"));
    }

    protected async Task Status_receipts_keep_invalid_and_state_follows_valid_only()
    {
        var store = await CreateAsync();
        var ok = Status("s/0", "r-3", 0, 1, "task_assigned", valid: true, tick: 5);
        var bad = Status("s/1", "r-3", 0, 4, "unload_done", valid: false, tick: 6);
        var result = await store.StoreBatchAsync(Batch(statuses: [ok, bad]), default);
        var again = await store.StoreBatchAsync(Batch(statuses: [ok]), default);

        result.StatusInserted.Should().Be(2);
        again.StatusDuplicate.Should().Be(1);
        var states = await store.GetRobotStatesAsync(Map, ["r-3", "r-x"], default);
        states.Should().ContainKey("r-3").And.HaveCount(1);
        states["r-3"].Status.Should().Be(1);
        states["r-3"].LastEventId.Should().Be("s/0");
        (await store.GetExistingStatusEventIdsAsync(["s/0", "s/1", "s/2"], default)).Should().BeEquivalentTo(["s/0", "s/1"]);
    }

    protected async Task Duplicate_conflict_is_ignored()
    {
        var store = await CreateAsync();
        var c = new ConflictRow("c/0", Map, 7, At, EventType.ConflictDetected,
            FleetJson.ToElement(new ConflictDetectedPayload("vertex", 3, 4, ["r-1", "r-2"], "r-2", "wait", 500)));
        (await store.StoreBatchAsync(Batch(conflicts: [c]), default)).ConflictInserted.Should().Be(1);
        (await store.StoreBatchAsync(Batch(conflicts: [c]), default)).ConflictDuplicate.Should().Be(1);
    }

    protected static StoreBatch Batch(IReadOnlyList<TelemetryRow>? telemetry = null, IReadOnlyList<TaskEventRow>? tasks = null,
        IReadOnlyList<StatusChangeRow>? statuses = null, IReadOnlyList<ConflictRow>? conflicts = null) =>
        new(telemetry ?? [], tasks ?? [], statuses ?? [], conflicts ?? []);

    protected static TelemetryRow Telemetry(string robot, long tick, short status) =>
        new(Map, robot, tick, 1, 1, 0, status, 90, At.AddMilliseconds(tick), 100);

    protected static TaskEventRow TaskEvent<T>(string eventId, string taskId, string type, T payload) =>
        new(eventId, Map, taskId, type, 1, At, FleetJson.ToElement(payload));

    protected static StatusChangeRow Status(string eventId, string robot, short from, short to, string reason, bool valid, long tick) =>
        new(eventId, Map, robot, tick, At, from, to, reason, valid);
}
