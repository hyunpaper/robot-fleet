using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using RobotFleet.Shared.Messaging;
using RobotFleet.Shared.Pipeline;

namespace RobotFleet.Infrastructure.Postgres;

/// unnest 배치 INSERT · ON CONFLICT DO NOTHING RETURNING으로 **새로 들어간 행만** 집계. 배치 하나 = 트랜잭션 하나. 10 §6 · §8
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
        var metrics = new Dictionary<(string Map, string Robot, DateTimeOffset Minute), long[]>();

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var (telIns, telDup) = await TelemetryAsync(conn, tx, batch.Telemetry, metrics, ct);
        var (taskIns, taskDup) = await TasksAsync(conn, tx, batch.TaskEvents, histories, metrics, ct);
        var (stIns, stDup) = await StatusesAsync(conn, tx, batch.StatusChanges, ct);
        var (cfIns, cfDup) = await ConflictsAsync(conn, tx, conflicts, ct);
        await MetricsAsync(conn, tx, metrics, ct);

        await tx.CommitAsync(ct);
        return new StoreResult(telIns, telDup, taskIns, taskDup, stIns, stDup, cfIns, cfDup);
    }

    public async Task<IReadOnlyDictionary<string, RobotStateRow>> GetRobotStatesAsync(string mapId,
        IReadOnlyCollection<string> robotIds, CancellationToken ct)
    {
        await using var cmd = _dataSource.CreateCommand(
            "SELECT robot_id, status, last_tick, last_event_id FROM robot_state WHERE map_id = @map AND robot_id = ANY(@ids)");
        cmd.Parameters.AddWithValue("map", mapId);
        cmd.Parameters.AddWithValue("ids", robotIds.ToArray());
        var found = new Dictionary<string, RobotStateRow>(StringComparer.Ordinal);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var robot = reader.GetString(0);
            found[robot] = new RobotStateRow(mapId, robot, reader.GetInt16(1), reader.GetInt64(2), reader.GetString(3));
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

    private static async Task<(int, int)> TelemetryAsync(NpgsqlConnection conn, NpgsqlTransaction tx, IReadOnlyList<TelemetryRow> rows,
        Dictionary<(string, string, DateTimeOffset), long[]> metrics, CancellationToken ct)
    {
        if (rows.Count == 0) return (0, 0);
        const string sql = """
            INSERT INTO robot_telemetry (map_id, robot_id, tick, x, y, heading, status, battery, occurred_at)
            SELECT * FROM unnest(@map, @robot, @tick, @x, @y, @heading, @status, @battery, @at)
            ON CONFLICT DO NOTHING
            RETURNING map_id, robot_id, tick, status, occurred_at
            """;
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("map", rows.Select(r => r.MapId).ToArray());
        cmd.Parameters.AddWithValue("robot", rows.Select(r => r.RobotId).ToArray());
        cmd.Parameters.AddWithValue("tick", rows.Select(r => r.Tick).ToArray());
        cmd.Parameters.AddWithValue("x", rows.Select(r => r.X).ToArray());
        cmd.Parameters.AddWithValue("y", rows.Select(r => r.Y).ToArray());
        cmd.Parameters.AddWithValue("heading", rows.Select(r => r.Heading).ToArray());
        cmd.Parameters.AddWithValue("status", rows.Select(r => r.Status).ToArray());
        cmd.Parameters.Add(new NpgsqlParameter("battery", NpgsqlDbType.Array | NpgsqlDbType.Smallint) { Value = rows.Select(r => r.Battery).ToArray() });
        cmd.Parameters.AddWithValue("at", rows.Select(r => r.OccurredAt.ToUniversalTime()).ToArray());

        var tickMs = rows.GroupBy(r => (r.MapId, r.RobotId, r.Tick)).ToDictionary(g => g.Key, g => g.First().TickMs);
        var inserted = 0;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                inserted++;
                var (map, robot, tick, status) = (reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt16(3));
                if (StoreMapping.BucketOf(status) is { } bucket)
                    Add(metrics, map, robot, reader.GetFieldValue<DateTimeOffset>(4), (int)bucket, tickMs[(map, robot, tick)]);
            }
        }

        return (inserted, rows.Count - inserted);
    }

    private static async Task<(int, int)> TasksAsync(NpgsqlConnection conn, NpgsqlTransaction tx, IReadOnlyList<TaskEventRow> rows,
        Dictionary<string, TaskHistoryChange> histories, Dictionary<(string, string, DateTimeOffset), long[]> metrics, CancellationToken ct)
    {
        if (rows.Count == 0) return (0, 0);
        const string receipt = """
            INSERT INTO task_event_log (event_id, map_id, task_id, event_type, tick, occurred_at)
            SELECT * FROM unnest(@id, @map, @task, @type, @tick, @at)
            ON CONFLICT (event_id) DO NOTHING
            RETURNING event_id
            """;
        var inserted = new HashSet<string>(StringComparer.Ordinal);
        await using (var cmd = new NpgsqlCommand(receipt, conn, tx))
        {
            cmd.Parameters.AddWithValue("id", rows.Select(r => r.EventId).ToArray());
            cmd.Parameters.AddWithValue("map", rows.Select(r => r.MapId).ToArray());
            cmd.Parameters.AddWithValue("task", rows.Select(r => r.TaskId).ToArray());
            cmd.Parameters.AddWithValue("type", rows.Select(r => r.EventType).ToArray());
            cmd.Parameters.AddWithValue("tick", rows.Select(r => r.Tick).ToArray());
            cmd.Parameters.AddWithValue("at", rows.Select(r => r.OccurredAt.ToUniversalTime()).ToArray());
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                inserted.Add(reader.GetString(0));
        }

        // 새로 들어간 이벤트만, 도착 순서대로 단조 upsert (01 워커 2차 방어)
        const string history = """
            INSERT INTO task_history (task_id, map_id, robot_id, task_type, origin_x, origin_y, dest_x, dest_y,
                                      created_at, assigned_at, started_at, completed_at, status, path_length, actual_steps, wait_ms)
            VALUES (@task, @map, @robot, COALESCE(@type, 0), @ox, @oy, @dx, @dy,
                    COALESCE(@created, @at), @assigned, @started, @completed, @status, @path, @steps, @wait)
            ON CONFLICT (task_id) DO UPDATE SET
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
                Add(metrics, row.MapId, robot, row.OccurredAt, 4, 1);
        }

        if (batch.BatchCommands.Count > 0) await batch.ExecuteNonQueryAsync(ct);
        return (inserted.Count, rows.Count - inserted.Count);
    }

    private static async Task<(int, int)> StatusesAsync(NpgsqlConnection conn, NpgsqlTransaction tx, IReadOnlyList<StatusChangeRow> rows,
        CancellationToken ct)
    {
        if (rows.Count == 0) return (0, 0);
        const string receipt = """
            INSERT INTO robot_status_log (event_id, map_id, robot_id, tick, from_status, to_status, reason, valid, occurred_at)
            SELECT * FROM unnest(@id, @map, @robot, @tick, @from, @to, @reason, @valid, @at)
            ON CONFLICT (event_id) DO NOTHING
            RETURNING event_id
            """;
        var inserted = new HashSet<string>(StringComparer.Ordinal);
        await using (var cmd = new NpgsqlCommand(receipt, conn, tx))
        {
            cmd.Parameters.AddWithValue("id", rows.Select(r => r.EventId).ToArray());
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
            .GroupBy(r => (r.MapId, r.RobotId)).Select(g => g.Last()).ToList();
        if (latest.Count > 0)
        {
            const string state = """
                INSERT INTO robot_state (map_id, robot_id, status, last_tick, last_event_id)
                SELECT * FROM unnest(@map, @robot, @status, @tick, @event)
                ON CONFLICT (map_id, robot_id) DO UPDATE SET
                    status = EXCLUDED.status, last_tick = EXCLUDED.last_tick, last_event_id = EXCLUDED.last_event_id
                """;
            await using var cmd = new NpgsqlCommand(state, conn, tx);
            cmd.Parameters.AddWithValue("map", latest.Select(r => r.MapId).ToArray());
            cmd.Parameters.AddWithValue("robot", latest.Select(r => r.RobotId).ToArray());
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
            INSERT INTO conflict_log (event_id, map_id, tick, conflict_type, x, y, robot_ids, resolution, wait_ms, occurred_at)
            VALUES (@id, @map, @tick, @type, @x, @y, @robots, @resolution, @wait, @at)
            ON CONFLICT (event_id) DO NOTHING
            """;
        await using var batch = new NpgsqlBatch(conn, tx);
        foreach (var r in rows)
        {
            var c = new NpgsqlBatchCommand(sql);
            c.Parameters.AddWithValue("id", r.EventId);
            c.Parameters.AddWithValue("map", r.MapId);
            c.Parameters.AddWithValue("tick", r.Tick);
            c.Parameters.AddWithValue("type", r.ConflictType);
            c.Parameters.Add(Nullable("x", NpgsqlDbType.Smallint, r.X));
            c.Parameters.Add(Nullable("y", NpgsqlDbType.Smallint, r.Y));
            c.Parameters.AddWithValue("robots", r.RobotIds.ToArray());
            c.Parameters.Add(Nullable("resolution", NpgsqlDbType.Smallint, r.Resolution));
            c.Parameters.Add(Nullable("wait", NpgsqlDbType.Integer, r.WaitMs));
            c.Parameters.AddWithValue("at", r.OccurredAt.ToUniversalTime());
            batch.BatchCommands.Add(c);
        }

        await batch.ExecuteNonQueryAsync(ct);
        var inserted = batch.BatchCommands.Cast<NpgsqlBatchCommand>().Sum(c => (int)c.RecordsAffected);
        return (inserted, rows.Count - inserted);
    }

    private static async Task MetricsAsync(NpgsqlConnection conn, NpgsqlTransaction tx,
        Dictionary<(string Map, string Robot, DateTimeOffset Minute), long[]> metrics, CancellationToken ct)
    {
        if (metrics.Count == 0) return;
        const string sql = """
            INSERT INTO metrics_minute (map_id, robot_id, minute, move_ms, work_ms, wait_ms, idle_ms, task_count)
            SELECT * FROM unnest(@map, @robot, @minute, @move, @work, @wait, @idle, @tasks)
            ON CONFLICT (map_id, robot_id, minute) DO UPDATE SET
                move_ms = metrics_minute.move_ms + EXCLUDED.move_ms,
                work_ms = metrics_minute.work_ms + EXCLUDED.work_ms,
                wait_ms = metrics_minute.wait_ms + EXCLUDED.wait_ms,
                idle_ms = metrics_minute.idle_ms + EXCLUDED.idle_ms,
                task_count = metrics_minute.task_count + EXCLUDED.task_count
            """;
        var list = metrics.ToList();
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
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

    // 칸: 0 move · 1 work · 2 wait · 3 idle · 4 task_count (MetricsBucket 순서와 같다)
    private static void Add(Dictionary<(string, string, DateTimeOffset), long[]> metrics, string map, string robot,
        DateTimeOffset at, int slot, long value)
    {
        var key = (map, robot, StoreMapping.MinuteOf(at));
        if (!metrics.TryGetValue(key, out var v)) metrics[key] = v = new long[5];
        v[slot] += value;
    }

    private static NpgsqlParameter Nullable<T>(string name, NpgsqlDbType type, T? value) where T : struct =>
        new(name, type) { Value = value.HasValue ? value.Value : DBNull.Value };
}
