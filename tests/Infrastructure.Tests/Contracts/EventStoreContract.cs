using FluentAssertions;
using RobotFleet.Shared.Messaging;
using RobotFleet.Shared.Pipeline;

namespace RobotFleet.Infrastructure.Tests.Contracts;

/// IEventStore 계약 — W-10~W-12 · 중복 · RETURNING 집계(C-03). 실제 PostgreSQL. 10 §6 · §12
public abstract class EventStoreContract
{
    protected const string Map = "m-contract";
    protected const string Run = "20261012-091423-r3";
    protected const string RunB = "20261012-101500-r3";
    protected static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-10-12T09:14:23.412Z");

    protected abstract Task<IEventStore> CreateAsync();

    /// metrics_minute (move_ms, wait_ms, task_count) 의 해당 분 (run = Run)
    protected abstract Task<(long MoveMs, long WaitMs, long TaskCount)> ReadMetricsAsync(string robotId, DateTimeOffset minute);

    /// task_history.status · robot_id, 없으면 null
    protected abstract Task<(short Status, string? RobotId)?> ReadTaskAsync(string runId, string taskId);

    /// simulations (map_code, robots, started_at 유무, ended_at 유무), 없으면 null
    protected abstract Task<(string MapCode, int? Robots, bool Started, bool Ended)?> ReadSimulationAsync(string runId);

    protected async Task Duplicate_telemetry_is_counted_once_in_metrics()   // W-10
    {
        var store = await CreateAsync();
        var row = Telemetry("r-1", tick: 10, status: 1);
        var first = await store.StoreBatchAsync(Batch(telemetry: [row, Telemetry("r-1", 11, 2)]), default);
        var again = await store.StoreBatchAsync(Batch(telemetry: [row]), default);

        first.TelemetryInserted.Should().Be(2);
        again.Should().Be(new StoreResult(0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0));
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
        (await ReadTaskAsync(Run, "T-1")).Should().Be(((short)3, "r-1"));
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

        (await ReadTaskAsync(Run, "T-2")).Should().Be(((short)2, "r-2"));
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
        var states = await store.GetRobotStatesAsync(Run, ["r-3", "r-x"], default);
        states.Should().ContainKey("r-3").And.HaveCount(1);
        states["r-3"].Status.Should().Be(1);
        states["r-3"].LastEventId.Should().Be("s/0");
        (await store.GetExistingStatusEventIdsAsync(["s/0", "s/1", "s/2"], default)).Should().BeEquivalentTo(["s/0", "s/1"]);
    }

    protected async Task Duplicate_conflict_is_ignored()
    {
        var store = await CreateAsync();
        var c = new ConflictRow("c/0", Run, Map, 7, At, EventType.ConflictDetected,
            FleetJson.ToElement(new ConflictDetectedPayload("vertex", 3, 4, ["r-1", "r-2"], "r-2", "wait", 500)));
        (await store.StoreBatchAsync(Batch(conflicts: [c]), default)).ConflictInserted.Should().Be(1);
        (await store.StoreBatchAsync(Batch(conflicts: [c]), default)).ConflictDuplicate.Should().Be(1);
    }

    protected async Task Robot_state_is_separated_by_run()   // R-run-01 (저장소 측)
    {
        var store = await CreateAsync();
        await store.StoreBatchAsync(Batch(statuses: [Status("a/0", "R-001", 0, 1, "task_assigned", valid: true, tick: 5)]), default);

        (await store.GetRobotStatesAsync(RunB, ["R-001"], default)).Should().BeEmpty("run B는 run A의 마지막 상태를 보지 않는다");
        await store.StoreBatchAsync(Batch(statuses: [Status("b/0", "R-001", 0, 1, "task_assigned", valid: true, tick: 1, run: RunB)]),
            default);

        (await store.GetRobotStatesAsync(Run, ["R-001"], default))["R-001"].LastEventId.Should().Be("a/0");
        (await store.GetRobotStatesAsync(RunB, ["R-001"], default))["R-001"].LastEventId.Should().Be("b/0");
    }

    protected async Task Same_task_id_in_new_run_is_not_blocked()   // R-run-02
    {
        var store = await CreateAsync();
        var created = new TaskCreatedPayload("T-00001", 0, new CellDto(1, 2), new CellDto(3, 4), 0);
        await store.StoreBatchAsync(Batch(tasks:
        [
            TaskEvent("ra/0", "T-00001", EventType.TaskCreated, created),
            TaskEvent("ra/1", "T-00001", EventType.TaskCompleted, new TaskCompletedPayload("T-00001", "R-001", 7, 7000, 0, 4000, 0)),
        ]), default);
        await store.StoreBatchAsync(Batch(tasks: [TaskEvent("rb/0", "T-00001", EventType.TaskCreated, created, RunB)]), default);

        (await ReadTaskAsync(Run, "T-00001")).Should().Be(((short)3, "R-001"));
        (await ReadTaskAsync(RunB, "T-00001")).Should().Be(((short)0, (string?)null), "run A의 Completed가 run B의 Pending을 막지 않는다");
    }

    protected async Task Telemetry_before_simulation_started_is_stored_then_meta_filled()   // R-run-03
    {
        var store = await CreateAsync();
        (await store.StoreBatchAsync(Batch(telemetry: [Telemetry("R-001", 1, 1)]), default)).TelemetryInserted.Should().Be(1);
        (await ReadSimulationAsync(Run)).Should().Be((Map, (int?)null, false, false), "최소 행(run_id · map_code)이 먼저 생긴다");

        var started = new SimulationStartedPayload(3, 1.0, 100, 7, "local", "", Map, 1, "sha256:00", "", []);
        var ended = new SimulationEndedPayload(600, 10, 9, 1, 0, 0, "max_ticks");
        var result = await store.StoreBatchAsync(Batch(runs:
        [
            new RunEventRow("s/0", Run, Map, EventType.SimulationStarted, At, FleetJson.ToElement(started)),
            new RunEventRow("s/1", Run, Map, EventType.SimulationEnded, At.AddMinutes(1), FleetJson.ToElement(ended)),
        ]), default);

        result.RunEvents.Should().Be(2);
        (await ReadSimulationAsync(Run)).Should().Be((Map, (int?)3, true, true));
    }

    protected async Task Replan_is_stored_once()
    {
        var store = await CreateAsync();
        var r = new ReplanRow("p/0", Run, Map, 9, At,
            FleetJson.ToElement(new PathReplannedPayload("R-001", "T-00001", "blocked", 10, 12, 0.4)));
        (await store.StoreBatchAsync(Batch(replans: [r]), default)).ReplanInserted.Should().Be(1);
        (await store.StoreBatchAsync(Batch(replans: [r]), default)).ReplanDuplicate.Should().Be(1);
    }

    protected static StoreBatch Batch(IReadOnlyList<TelemetryRow>? telemetry = null, IReadOnlyList<TaskEventRow>? tasks = null,
        IReadOnlyList<StatusChangeRow>? statuses = null, IReadOnlyList<ConflictRow>? conflicts = null,
        IReadOnlyList<ReplanRow>? replans = null, IReadOnlyList<RunEventRow>? runs = null) =>
        new(telemetry ?? [], tasks ?? [], statuses ?? [], conflicts ?? [], replans ?? [], runs ?? []);

    protected static TelemetryRow Telemetry(string robot, long tick, short status, string run = Run) =>
        new(run, Map, robot, tick, 1, 1, 0, status, 90, At.AddMilliseconds(tick), 100);

    protected static TaskEventRow TaskEvent<T>(string eventId, string taskId, string type, T payload, string run = Run) =>
        new(eventId, run, Map, taskId, type, 1, At, FleetJson.ToElement(payload));

    protected static StatusChangeRow Status(string eventId, string robot, short from, short to, string reason, bool valid, long tick,
        string run = Run) =>
        new(eventId, run, Map, robot, tick, At, from, to, reason, valid);
}
