using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using RobotFleet.Shared.Messaging;
using RobotFleet.Shared.Pipeline;

namespace RobotFleet.Infrastructure.Postgres;

/// unnest 배치 INSERT · ON CONFLICT DO NOTHING RETURNING으로 **새로 들어간 행만** 집계. 배치 하나 = 트랜잭션 하나. 10 §6 · §8.
/// 모든 사실 테이블 키는 run_id가 선두다(01). 봉투의 mapId는 map_code 컬럼에 싣는다
public sealed class PgEventStore : IEventStore
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<PgEventStore> _logger;

    public PgEventStore(NpgsqlDataSource dataSource, ILogger<PgEventStore> logger)
    {
        _dataSource = dataSource;
        _logger = logger;
    }

    public async Task<StoreResult> StoreBatchAsync(StoreBatch batch, CancellationToken ct)
    {
        // 해석은 트랜잭션 전에(실패하면 아무것도 쓰지 않는다)
        var histories = batch.TaskEvents.ToDictionary(e => e.EventId, StoreMapping.ToHistory);
        var conflicts = batch.Conflicts.Select(StoreMapping.ToConflict).ToList();
        var replans = batch.Replans.Select(StoreMapping.ToReplan).ToList();
        var runEvents = batch.RunEvents.Select(r => (Row: r, Payload: StoreMapping.ToRunEvent(r))).ToList();
        var metrics = new Dictionary<MetricsKey, long[]>();

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await EnsureRunsAsync(conn, tx, batch, ct);
        var (telIns, telDup) = await TelemetryAsync(conn, tx, batch.Telemetry, metrics, ct);
        var (taskIns, taskDup) = await TasksAsync(conn, tx, batch.TaskEvents, histories, metrics, ct);
        var (stIns, stDup) = await StatusesAsync(conn, tx, batch.StatusChanges, ct);
        var (cfIns, cfDup) = await ConflictsAsync(conn, tx, conflicts, ct);
        var (rpIns, rpDup) = await ReplansAsync(conn, tx, replans, ct);
        await MetricsAsync(conn, tx, metrics, ct);
        foreach (var (row, payload) in runEvents)
            await RunEventAsync(conn, tx, row, payload, ct);

        await tx.CommitAsync(ct);
        return new StoreResult(telIns, telDup, taskIns, taskDup, stIns, stDup, cfIns, cfDup, rpIns, rpDup, runEvents.Count);
    }

    public async Task<IReadOnlyDictionary<string, RobotStateRow>> GetRobotStatesAsync(string runId,
        IReadOnlyCollection<string> robotIds, CancellationToken ct)
    {
        await using var cmd = _dataSource.CreateCommand(
            "SELECT robot_id, map_code, status, last_tick, last_event_id FROM robot_state WHERE run_id = @run AND robot_id = ANY(@ids)");
        cmd.Parameters.AddWithValue("run", runId);
        cmd.Parameters.AddWithValue("ids", robotIds.ToArray());
        var found = new Dictionary<string, RobotStateRow>(StringComparer.Ordinal);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var robot = reader.GetString(0);
            found[robot] = new RobotStateRow(runId, reader.GetString(1), robot, reader.GetInt16(2), reader.GetInt64(3), reader.GetString(4));
        }

        return found;
    }

    public async Task<IReadOnlySet<string>> GetExistingStatusEventIdsAsync(IReadOnlyCollection<string> eventIds, CancellationToken ct)
    {
        await using var cmd = _dataSource.CreateCommand("SELECT event_id FROM robot_status_log WHERE event_id = ANY(@ids)");
        cmd.Parameters.AddWithValue("ids", eventIds.ToArray());
        var found = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            found.Add(reader.GetString(0));
        return found;
    }

    // simulations로 FK를 걸지 않으므로, 배치에 나온 run마다 run_id · map_code만 가진 최소 행을 먼저 만든다.
    // SimulationStarted가 telemetry보다 늦게 와도 적재가 막히지 않고, 메타는 나중에 채워진다(10 §6, R-run-03)
    private static async Task EnsureRunsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, StoreBatch batch, CancellationToken ct)
    {
        var runs = new Dictionary<string, string>(StringComparer.Ordinal);
        void Add(string run, string map) => runs.TryAdd(run, map);
        foreach (var r in batch.Telemetry) Add(r.RunId, r.MapId);
        foreach (var r in batch.TaskEvents) Add(r.RunId, r.MapId);
        foreach (var r in batch.StatusChanges) Add(r.RunId, r.MapId);
        foreach (var r in batch.Conflicts) Add(r.RunId, r.MapId);
        foreach (var r in batch.Replans) Add(r.RunId, r.MapId);
        foreach (var r in batch.RunEvents) Add(r.RunId, r.MapId);
        if (runs.Count == 0) return;

        const string sql = """
            INSERT INTO simulations (run_id, map_code)
            SELECT * FROM unnest(@run, @map)
            ON CONFLICT (run_id) DO NOTHING
            """;
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("run", runs.Keys.ToArray());
        cmd.Parameters.AddWithValue("map", runs.Values.ToArray());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<(int, int)> TelemetryAsync(NpgsqlConnection conn, NpgsqlTransaction tx, IReadOnlyList<TelemetryRow> rows,
        Dictionary<MetricsKey, long[]> metrics, CancellationToken ct)
    {
        if (rows.Count == 0) return (0, 0);
        const string sql = """
            INSERT INTO robot_telemetry (run_id, map_code, robot_id, tick, x, y, heading, status, battery, occurred_at)
            SELECT * FROM unnest(@run, @map, @robot, @tick, @x, @y, @heading, @status, @battery, @at)
            ON CONFLICT DO NOTHING
            RETURNING run_id, map_code, robot_id, tick, status, occurred_at
            """;
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("run", rows.Select(r => r.RunId).ToArray());
        cmd.Parameters.AddWithValue("map", rows.Select(r => r.MapId).ToArray());
        cmd.Parameters.AddWithValue("robot", rows.Select(r => r.RobotId).ToArray());
        cmd.Parameters.AddWithValue("tick", rows.Select(r => r.Tick).ToArray());
        cmd.Parameters.AddWithValue("x", rows.Select(r => r.X).ToArray());
        cmd.Parameters.AddWithValue("y", rows.Select(r => r.Y).ToArray());
        cmd.Parameters.AddWithValue("heading", rows.Select(r => r.Heading).ToArray());
        cmd.Parameters.AddWithValue("status", rows.Select(r => r.Status).ToArray());
        cmd.Parameters.Add(new NpgsqlParameter("battery", NpgsqlDbType.Array | NpgsqlDbType.Smallint) { Value = rows.Select(r => r.Battery).ToArray() });
        cmd.Parameters.AddWithValue("at", rows.Select(r => r.OccurredAt.ToUniversalTime()).ToArray());

        var tickMs = rows.GroupBy(r => (r.RunId, r.RobotId, r.Tick)).ToDictionary(g => g.Key, g => g.First().TickMs);
        var inserted = 0;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                inserted++;
                var (run, map, robot, tick, status) =
                    (reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetInt16(4));
                if (StoreMapping.BucketOf(status) is { } bucket)
                    Add(metrics, run, map, robot, reader.GetFieldValue<DateTimeOffset>(5), (int)bucket, tickMs[(run, robot, tick)]);
            }
        }

        return (inserted, rows.Count - inserted);
    }

    private static async Task<(int, int)> TasksAsync(NpgsqlConnection conn, NpgsqlTransaction tx, IReadOnlyList<TaskEventRow> rows,
        Dictionary<string, TaskHistoryChange> histories, Dictionary<MetricsKey, long[]> metrics, CancellationToken ct)
    {
        if (rows.Count == 0) return (0, 0);
        const string receipt = """
            INSERT INTO task_event_log (event_id, run_id, map_code, task_id, event_type, tick, occurred_at)
            SELECT * FROM unnest(@id, @run, @map, @task, @type, @tick, @at)
            ON CONFLICT (event_id) DO NOTHING
            RETURNING event_id
            """;
        var inserted = new HashSet<string>(StringComparer.Ordinal);
        await using (var cmd = new NpgsqlCommand(receipt, conn, tx))
        {
            cmd.Parameters.AddWithValue("id", rows.Select(r => r.EventId).ToArray());
            cmd.Parameters.AddWithValue("run", rows.Select(r => r.RunId).ToArray());
            cmd.Parameters.AddWithValue("map", rows.Select(r => r.MapId).ToArray());
            cmd.Parameters.AddWithValue("task", rows.Select(r => r.TaskId).ToArray());
            cmd.Parameters.AddWithValue("type", rows.Select(r => r.EventType).ToArray());
            cmd.Parameters.AddWithValue("tick", rows.Select(r => r.Tick).ToArray());
            cmd.Parameters.AddWithValue("at", rows.Select(r => r.OccurredAt.ToUniversalTime()).ToArray());
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                inserted.Add(reader.GetString(0));
        }

        // 새로 들어간 이벤트만, 도착 순서대로 단조 upsert (01 워커 2차 방어). 키 = (run_id, task_id):
        // run이 바뀌면 같은 taskId라도 다른 행이라 이전 run의 Completed가 새 run의 Pending을 막지 않는다(R-run-02)
        const string history = """
            INSERT INTO task_history (run_id, task_id, map_code, robot_id, task_type, origin_x, origin_y, dest_x, dest_y,
                                      created_at, assigned_at, started_at, completed_at, status, path_length, actual_steps, wait_ms)
            VALUES (@run, @task, @map, @robot, COALESCE(@type, 0), @ox, @oy, @dx, @dy,
                    COALESCE(@created, @at), @assigned, @started, @completed, @status, @path, @steps, @wait)
            ON CONFLICT (run_id, task_id) DO UPDATE SET
                robot_id = COALESCE(EXCLUDED.robot_id, task_history.robot_id),
                assigned_at = COALESCE(EXCLUDED.assigned_at, task_history.assigned_at),
                started_at = COALESCE(EXCLUDED.started_at, task_history.started_at),
                completed_at = COALESCE(EXCLUDED.completed_at, task_history.completed_at),
                status = EXCLUDED.status,
                path_length = COALESCE(EXCLUDED.path_length, task_history.path_length),
                actual_steps = COALESCE(EXCLUDED.actual_steps, task_history.actual_steps),
                wait_ms = COALESCE(EXCLUDED.wait_ms, task_history.wait_ms)
            WHERE EXCLUDED.status > task_history.status
            """;
        await using var batch = new NpgsqlBatch(conn, tx);
        foreach (var row in rows.Where(r => inserted.Contains(r.EventId)))
        {
            var h = histories[row.EventId];
            var c = new NpgsqlBatchCommand(history);
            c.Parameters.AddWithValue("run", h.RunId);
            c.Parameters.AddWithValue("task", h.TaskId);
            c.Parameters.AddWithValue("map", h.MapId);
            c.Parameters.AddWithValue("robot", (object?)h.RobotId ?? DBNull.Value);
            c.Parameters.Add(Nullable("type", NpgsqlDbType.Smallint, h.TaskType));
            c.Parameters.Add(Nullable("ox", NpgsqlDbType.Smallint, h.OriginX));
            c.Parameters.Add(Nullable("oy", NpgsqlDbType.Smallint, h.OriginY));
            c.Parameters.Add(Nullable("dx", NpgsqlDbType.Smallint, h.DestX));
            c.Parameters.Add(Nullable("dy", NpgsqlDbType.Smallint, h.DestY));
            c.Parameters.Add(Nullable("created", NpgsqlDbType.TimestampTz, h.CreatedAt?.ToUniversalTime()));
            c.Parameters.AddWithValue("at", row.OccurredAt.ToUniversalTime());
            c.Parameters.Add(Nullable("assigned", NpgsqlDbType.TimestampTz, h.AssignedAt?.ToUniversalTime()));
            c.Parameters.Add(Nullable("started", NpgsqlDbType.TimestampTz, h.StartedAt?.ToUniversalTime()));
            c.Parameters.Add(Nullable("completed", NpgsqlDbType.TimestampTz, h.CompletedAt?.ToUniversalTime()));
            c.Parameters.AddWithValue("status", h.Status);
            c.Parameters.Add(Nullable("path", NpgsqlDbType.Integer, h.PathLength));
            c.Parameters.Add(Nullable("steps", NpgsqlDbType.Integer, h.ActualSteps));
            c.Parameters.Add(Nullable("wait", NpgsqlDbType.Integer, h.WaitMs));
            batch.BatchCommands.Add(c);

            if (StoreMapping.CompletedRobotOf(row) is { } robot)
                Add(metrics, row.RunId, row.MapId, robot, row.OccurredAt, 4, 1);
        }

        if (batch.BatchCommands.Count > 0) await batch.ExecuteNonQueryAsync(ct);
        return (inserted.Count, rows.Count - inserted.Count);
    }

    private static async Task<(int, int)> StatusesAsync(NpgsqlConnection conn, NpgsqlTransaction tx, IReadOnlyList<StatusChangeRow> rows,
        CancellationToken ct)
    {
        if (rows.Count == 0) return (0, 0);
        const string receipt = """
            INSERT INTO robot_status_log (event_id, run_id, map_code, robot_id, tick, from_status, to_status, reason, valid, occurred_at)
            SELECT * FROM unnest(@id, @run, @map, @robot, @tick, @from, @to, @reason, @valid, @at)
            ON CONFLICT (event_id) DO NOTHING
            RETURNING event_id
            """;
        var inserted = new HashSet<string>(StringComparer.Ordinal);
        await using (var cmd = new NpgsqlCommand(receipt, conn, tx))
        {
            cmd.Parameters.AddWithValue("id", rows.Select(r => r.EventId).ToArray());
            cmd.Parameters.AddWithValue("run", rows.Select(r => r.RunId).ToArray());
            cmd.Parameters.AddWithValue("map", rows.Select(r => r.MapId).ToArray());
            cmd.Parameters.AddWithValue("robot", rows.Select(r => r.RobotId).ToArray());
            cmd.Parameters.AddWithValue("tick", rows.Select(r => r.Tick).ToArray());
            cmd.Parameters.AddWithValue("from", rows.Select(r => r.From).ToArray());
            cmd.Parameters.AddWithValue("to", rows.Select(r => r.To).ToArray());
            cmd.Parameters.AddWithValue("reason", rows.Select(r => r.Reason).ToArray());
            cmd.Parameters.AddWithValue("valid", rows.Select(r => r.Valid).ToArray());
            cmd.Parameters.AddWithValue("at", rows.Select(r => r.OccurredAt.ToUniversalTime()).ToArray());
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                inserted.Add(reader.GetString(0));
        }

        // 같은 로봇이 한 배치에 여러 번 바뀌면 마지막 유효 전이만(한 문장에서 같은 행을 두 번 갱신할 수 없다)
        var latest = rows.Where(r => r.Valid && inserted.Contains(r.EventId))
            .GroupBy(r => (r.RunId, r.RobotId)).Select(g => g.Last()).ToList();
        if (latest.Count > 0)
        {
            const string state = """
                INSERT INTO robot_state (run_id, robot_id, map_code, status, last_tick, last_event_id)
                SELECT * FROM unnest(@run, @robot, @map, @status, @tick, @event)
                ON CONFLICT (run_id, robot_id) DO UPDATE SET
                    status = EXCLUDED.status, last_tick = EXCLUDED.last_tick, last_event_id = EXCLUDED.last_event_id
                """;
            await using var cmd = new NpgsqlCommand(state, conn, tx);
            cmd.Parameters.AddWithValue("run", latest.Select(r => r.RunId).ToArray());
            cmd.Parameters.AddWithValue("robot", latest.Select(r => r.RobotId).ToArray());
            cmd.Parameters.AddWithValue("map", latest.Select(r => r.MapId).ToArray());
            cmd.Parameters.AddWithValue("status", latest.Select(r => r.To).ToArray());
            cmd.Parameters.AddWithValue("tick", latest.Select(r => r.Tick).ToArray());
            cmd.Parameters.AddWithValue("event", latest.Select(r => r.EventId).ToArray());
            await cmd.ExecuteNonQueryAsync(ct);
        }

        return (inserted.Count, rows.Count - inserted.Count);
    }

    private static async Task<(int, int)> ConflictsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, List<ConflictLogChange> rows,
        CancellationToken ct)
    {
        if (rows.Count == 0) return (0, 0);
        const string sql = """
            INSERT INTO conflict_log (event_id, run_id, map_code, tick, conflict_type, x, y, robot_ids, resolution,
                                      wait_ms, resolved_ms, yield_robot_id, yield_to_x, yield_to_y, occurred_at)
            VALUES (@id, @run, @map, @tick, @type, @x, @y, @robots, @resolution,
                    @wait, @resolved, @yield, @yx, @yy, @at)
            ON CONFLICT (event_id) DO NOTHING
            """;
        await using var batch = new NpgsqlBatch(conn, tx);
        foreach (var r in rows)
        {
            var c = new NpgsqlBatchCommand(sql);
            c.Parameters.AddWithValue("id", r.EventId);
            c.Parameters.AddWithValue("run", r.RunId);
            c.Parameters.AddWithValue("map", r.MapId);
            c.Parameters.AddWithValue("tick", r.Tick);
            c.Parameters.AddWithValue("type", r.ConflictType);
            c.Parameters.Add(Nullable("x", NpgsqlDbType.Smallint, r.X));
            c.Parameters.Add(Nullable("y", NpgsqlDbType.Smallint, r.Y));
            c.Parameters.AddWithValue("robots", r.RobotIds.ToArray());
            c.Parameters.Add(Nullable("resolution", NpgsqlDbType.Smallint, r.Resolution));
            c.Parameters.Add(Nullable("wait", NpgsqlDbType.Integer, r.WaitMs));
            c.Parameters.Add(Nullable("resolved", NpgsqlDbType.Integer, r.ResolvedMs));
            c.Parameters.Add(new NpgsqlParameter("yield", NpgsqlDbType.Text) { Value = (object?)r.YieldRobotId ?? DBNull.Value });
            c.Parameters.Add(Nullable("yx", NpgsqlDbType.Smallint, r.YieldToX));
            c.Parameters.Add(Nullable("yy", NpgsqlDbType.Smallint, r.YieldToY));
            c.Parameters.AddWithValue("at", r.OccurredAt.ToUniversalTime());
            batch.BatchCommands.Add(c);
        }

        await batch.ExecuteNonQueryAsync(ct);
        var inserted = batch.BatchCommands.Cast<NpgsqlBatchCommand>().Sum(c => (int)c.RecordsAffected);
        return (inserted, rows.Count - inserted);
    }

    // PathReplanned → replan_log (10 §6). 비영속 범주라 유실은 허용, 중복은 event_id로 막는다
    private static async Task<(int, int)> ReplansAsync(NpgsqlConnection conn, NpgsqlTransaction tx, List<ReplanChange> rows,
        CancellationToken ct)
    {
        if (rows.Count == 0) return (0, 0);
        const string sql = """
            INSERT INTO replan_log (event_id, run_id, map_code, robot_id, task_id, tick, reason, old_length, new_length, compute_ms, occurred_at)
            SELECT * FROM unnest(@id, @run, @map, @robot, @task, @tick, @reason, @old, @new, @compute, @at)
            ON CONFLICT (event_id) DO NOTHING
            RETURNING event_id
            """;
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("id", rows.Select(r => r.EventId).ToArray());
        cmd.Parameters.AddWithValue("run", rows.Select(r => r.RunId).ToArray());
        cmd.Parameters.AddWithValue("map", rows.Select(r => r.MapId).ToArray());
        cmd.Parameters.AddWithValue("robot", rows.Select(r => r.RobotId).ToArray());
        cmd.Parameters.Add(new NpgsqlParameter("task", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = rows.Select(r => r.TaskId).ToArray() });
        cmd.Parameters.AddWithValue("tick", rows.Select(r => r.Tick).ToArray());
        cmd.Parameters.AddWithValue("reason", rows.Select(r => r.Reason).ToArray());
        cmd.Parameters.AddWithValue("old", rows.Select(r => r.OldLength).ToArray());
        cmd.Parameters.AddWithValue("new", rows.Select(r => r.NewLength).ToArray());
        cmd.Parameters.AddWithValue("compute", rows.Select(r => (float)r.ComputeMs).ToArray());
        cmd.Parameters.AddWithValue("at", rows.Select(r => r.OccurredAt.ToUniversalTime()).ToArray());
        var inserted = 0;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            inserted++;
        return (inserted, rows.Count - inserted);
    }

    // SimulationStarted → 메타 UPSERT(최소 행이 이미 있다), SimulationEnded → 종료 통계 UPDATE. 둘 다 멱등
    private async Task RunEventAsync(NpgsqlConnection conn, NpgsqlTransaction tx, RunEventRow row, object payload, CancellationToken ct)
    {
        if (payload is SimulationStartedPayload s)
        {
            const string sql = """
                INSERT INTO simulations (run_id, map_code, environment, line_code, pattern_code, map_version, map_hash,
                                         robots, speed, tick_ms, task_seed, params, started_at)
                VALUES (@run, @map, @env, @line, @pattern, @version, @hash, @robots, @speed, @tickMs, @seed, @params, @at)
                ON CONFLICT (run_id) DO UPDATE SET
                    map_code = EXCLUDED.map_code, environment = EXCLUDED.environment, line_code = EXCLUDED.line_code,
                    pattern_code = EXCLUDED.pattern_code, map_version = EXCLUDED.map_version, map_hash = EXCLUDED.map_hash,
                    robots = EXCLUDED.robots, speed = EXCLUDED.speed, tick_ms = EXCLUDED.tick_ms, task_seed = EXCLUDED.task_seed,
                    params = EXCLUDED.params, started_at = EXCLUDED.started_at
                """;
            await using (var cmd = new NpgsqlCommand(sql, conn, tx))
            {
                cmd.Parameters.AddWithValue("run", row.RunId);
                cmd.Parameters.AddWithValue("map", row.MapId);
                cmd.Parameters.Add(Text("env", s.Environment));
                cmd.Parameters.Add(Text("line", s.LineCode));
                cmd.Parameters.Add(Text("pattern", s.TaskPatternCode));
                cmd.Parameters.AddWithValue("version", s.MapVersion);
                cmd.Parameters.Add(Text("hash", s.MapHash));
                cmd.Parameters.AddWithValue("robots", s.Robots);
                cmd.Parameters.AddWithValue("speed", (float)s.Speed);
                cmd.Parameters.AddWithValue("tickMs", s.TickMs);
                cmd.Parameters.AddWithValue("seed", s.TaskSeed);
                cmd.Parameters.Add(new NpgsqlParameter("params", NpgsqlDbType.Jsonb)
                {
                    Value = JsonSerializer.Serialize(s.Params ?? new Dictionary<string, JsonElement>()),
                });
                cmd.Parameters.AddWithValue("at", row.OccurredAt.ToUniversalTime());
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await CheckMapHashAsync(conn, tx, row, s, ct);
            return;
        }

        var e = (SimulationEndedPayload)payload;
        const string ended = """
            UPDATE simulations SET ended_at = @at, ticks = @ticks, tasks_created = @created, tasks_completed = @completed,
                                   tasks_failed = @failed, overruns = @overruns, stall_ms = @stall, end_reason = @reason
            WHERE run_id = @run
            """;
        await using var end = new NpgsqlCommand(ended, conn, tx);
        end.Parameters.AddWithValue("run", row.RunId);
        end.Parameters.AddWithValue("at", row.OccurredAt.ToUniversalTime());
        end.Parameters.AddWithValue("ticks", e.Ticks);
        end.Parameters.AddWithValue("created", e.TasksCreated);
        end.Parameters.AddWithValue("completed", e.TasksCompleted);
        end.Parameters.AddWithValue("failed", e.TasksFailed);
        end.Parameters.AddWithValue("overruns", (long)e.Overruns);
        end.Parameters.AddWithValue("stall", e.StallMs);
        end.Parameters.Add(Text("reason", e.Reason));
        await end.ExecuteNonQueryAsync(ct);
    }

    // 03: 맵 파일을 고치고 version을 안 올리면 과거 run 해석이 조용히 틀어진다. 기준정보에 행이 없으면 대조하지 않는다
    private async Task CheckMapHashAsync(NpgsqlConnection conn, NpgsqlTransaction tx, RunEventRow row, SimulationStartedPayload s,
        CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT map_hash FROM maps WHERE map_code = @map AND version = @version", conn, tx);
        cmd.Parameters.AddWithValue("map", row.MapId);
        cmd.Parameters.AddWithValue("version", s.MapVersion);
        if (await cmd.ExecuteScalarAsync(ct) is string stored && !string.Equals(stored, s.MapHash, StringComparison.Ordinal))
        {
            _logger.LogWarning("맵 해시 불일치: run {RunId} {MapId} v{Version} 송신 {Sent} · 기준정보 {Stored}",
                row.RunId, row.MapId, s.MapVersion, s.MapHash, stored);
        }
    }

    private static async Task MetricsAsync(NpgsqlConnection conn, NpgsqlTransaction tx,
        Dictionary<MetricsKey, long[]> metrics, CancellationToken ct)
    {
        if (metrics.Count == 0) return;
        const string sql = """
            INSERT INTO metrics_minute (run_id, map_code, robot_id, minute, move_ms, work_ms, wait_ms, idle_ms, task_count)
            SELECT * FROM unnest(@run, @map, @robot, @minute, @move, @work, @wait, @idle, @tasks)
            ON CONFLICT (run_id, robot_id, minute) DO UPDATE SET
                move_ms = metrics_minute.move_ms + EXCLUDED.move_ms,
                work_ms = metrics_minute.work_ms + EXCLUDED.work_ms,
                wait_ms = metrics_minute.wait_ms + EXCLUDED.wait_ms,
                idle_ms = metrics_minute.idle_ms + EXCLUDED.idle_ms,
                task_count = metrics_minute.task_count + EXCLUDED.task_count
            """;
        var list = metrics.ToList();
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("run", list.Select(m => m.Key.Run).ToArray());
        cmd.Parameters.AddWithValue("map", list.Select(m => m.Key.Map).ToArray());
        cmd.Parameters.AddWithValue("robot", list.Select(m => m.Key.Robot).ToArray());
        cmd.Parameters.AddWithValue("minute", list.Select(m => m.Key.Minute).ToArray());
        for (var i = 0; i < 5; i++)
        {
            var index = i;
            cmd.Parameters.AddWithValue(new[] { "move", "work", "wait", "idle", "tasks" }[i],
                list.Select(m => (int)m.Value[index]).ToArray());
        }

        await cmd.ExecuteNonQueryAsync(ct);
    }

    // PK는 (run_id, robot_id, minute). map_code는 run마다 하나라 키에 넣어도 행이 갈리지 않는다
    private readonly record struct MetricsKey(string Run, string Map, string Robot, DateTimeOffset Minute);

    // 칸: 0 move · 1 work · 2 wait · 3 idle · 4 task_count (MetricsBucket 순서와 같다)
    private static void Add(Dictionary<MetricsKey, long[]> metrics, string run, string map, string robot,
        DateTimeOffset at, int slot, long value)
    {
        var key = new MetricsKey(run, map, robot, StoreMapping.MinuteOf(at));
        if (!metrics.TryGetValue(key, out var v)) metrics[key] = v = new long[5];
        v[slot] += value;
    }

    private static NpgsqlParameter Nullable<T>(string name, NpgsqlDbType type, T? value) where T : struct =>
        new(name, type) { Value = value.HasValue ? value.Value : DBNull.Value };

    // 빈 문자열은 "값 없음"(기준정보 미적재 등)으로 NULL 저장
    private static NpgsqlParameter Text(string name, string? value) =>
        new(name, NpgsqlDbType.Text) { Value = string.IsNullOrEmpty(value) ? DBNull.Value : value };
}
