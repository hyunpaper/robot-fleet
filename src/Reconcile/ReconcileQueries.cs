using Npgsql;

namespace RobotFleet.Reconcile;

/// 송신 집합(임시 테이블 sent) − 적재 집합. 모든 조건에 run_id를 붙인다 — tick이 run마다 0부터 다시 시작하므로
/// run 조건이 없으면 이전 run의 행을 이번 run의 적재로 착각하거나 누락으로 센다. 10 §10
public sealed class ReconcileQueries : IReconcileQueries
{
    private const int SampleLimit = 100;

    // task 범주: Task* → task_event_log, RobotStatusChanged → robot_status_log(valid 무관 — 위반도 영수증이 있다),
    // SimulationStarted · Ended → simulations의 started_at · ended_at(영수증 테이블 없이 UPSERT로 멱등)
    private const string TaskFound = """
        EXISTS (SELECT 1 FROM task_event_log t WHERE t.event_id = s.event_id AND t.run_id = s.run_id)
        OR EXISTS (SELECT 1 FROM robot_status_log r WHERE r.event_id = s.event_id AND r.run_id = s.run_id)
        OR (s.event_type = 'SimulationStarted'
            AND EXISTS (SELECT 1 FROM simulations m WHERE m.run_id = s.run_id AND m.started_at IS NOT NULL))
        OR (s.event_type = 'SimulationEnded'
            AND EXISTS (SELECT 1 FROM simulations m WHERE m.run_id = s.run_id AND m.ended_at IS NOT NULL))
        """;

    private const string ConflictFound =
        "EXISTS (SELECT 1 FROM conflict_log c WHERE c.event_id = s.event_id AND c.run_id = s.run_id)";

    private const string TelemetryFound = """
        EXISTS (SELECT 1 FROM robot_telemetry r WHERE r.run_id = s.run_id AND r.robot_id = s.key AND r.tick = s.tick)
        """;

    // 시간 분해는 metrics_minute, 작업은 task_history, 충돌 · 교착은 conflict_log, 재계획은 replan_log, 조건 · 시스템은 simulations.
    // 처리량은 시뮬레이션 시간(ticks × tick_ms) 기준 — 배속과 무관하게 비교된다. 리드타임은 occurredAt 차(ms)
    private const string RunMetricsSql = """
        WITH m AS (
            SELECT sum(move_ms)::bigint AS mv, sum(work_ms)::bigint AS wk, sum(wait_ms)::bigint AS wt, sum(idle_ms)::bigint AS idl
            FROM metrics_minute WHERE run_id = @run),
        t AS (
            SELECT count(*) FILTER (WHERE status = 3) AS done,
                   count(*) FILTER (WHERE status = 4) AS failed,
                   percentile_cont(0.5) WITHIN GROUP (ORDER BY extract(epoch FROM completed_at - created_at) * 1000)
                       FILTER (WHERE status = 3) AS p50,
                   percentile_cont(0.9) WITHIN GROUP (ORDER BY extract(epoch FROM completed_at - created_at) * 1000)
                       FILTER (WHERE status = 3) AS p90,
                   percentile_cont(0.99) WITHIN GROUP (ORDER BY extract(epoch FROM completed_at - created_at) * 1000)
                       FILTER (WHERE status = 3) AS p99,
                   sum(path_length) FILTER (WHERE status = 3) AS path,
                   sum(actual_steps) FILTER (WHERE status = 3) AS steps
            FROM task_history WHERE run_id = @run),
        c AS (
            SELECT count(*) FILTER (WHERE conflict_type <> 3) AS conflicts,
                   sum(wait_ms) FILTER (WHERE conflict_type <> 3) AS wait_sum,
                   count(*) FILTER (WHERE conflict_type = 3) AS deadlocks,
                   avg(resolved_ms) FILTER (WHERE conflict_type = 3) AS resolve_avg
            FROM conflict_log WHERE run_id = @run),
        r AS (SELECT count(*) AS replans FROM replan_log WHERE run_id = @run),
        s AS (SELECT ticks, tick_ms, overruns, stall_ms, ended_at FROM simulations WHERE run_id = @run)
        INSERT INTO run_metrics (run_id, complete, move_ms, work_ms, wait_ms, idle_ms, utilization, wait_ratio, idle_ratio,
                                 wait_count, wait_ms_avg, throughput_per_hour, lead_time_p50, lead_time_p90, lead_time_p99,
                                 path_efficiency, task_failed_ratio, conflict_count, deadlock_count, deadlock_resolve_ms_avg,
                                 replan_count, replan_per_task, tick_overruns, stall_ms, computed_at)
        SELECT @run, (s.ended_at IS NOT NULL), m.mv, m.wk, m.wt, m.idl,
               ((m.mv + m.wk)::double precision / NULLIF(m.mv + m.wk + m.wt + m.idl, 0))::real,
               (m.wt::double precision / NULLIF(m.mv + m.wk + m.wt + m.idl, 0))::real,
               (m.idl::double precision / NULLIF(m.mv + m.wk + m.wt + m.idl, 0))::real,
               c.conflicts::int,
               (c.wait_sum::double precision / NULLIF(c.conflicts, 0))::real,
               (t.done / NULLIF(s.ticks * s.tick_ms / 3600000.0, 0))::real,
               round(t.p50)::int, round(t.p90)::int, round(t.p99)::int,
               (t.path::double precision / NULLIF(t.steps, 0))::real,
               (t.failed::double precision / NULLIF(t.done + t.failed, 0))::real,
               c.conflicts::int, c.deadlocks::int, c.resolve_avg::real,
               r.replans::int, (r.replans::double precision / NULLIF(t.done, 0))::real,
               s.overruns, s.stall_ms, now()
        FROM m CROSS JOIN t CROSS JOIN c CROSS JOIN r LEFT JOIN s ON true
        ON CONFLICT (run_id) DO UPDATE SET
            complete = EXCLUDED.complete,
            move_ms = EXCLUDED.move_ms, work_ms = EXCLUDED.work_ms, wait_ms = EXCLUDED.wait_ms, idle_ms = EXCLUDED.idle_ms,
            utilization = EXCLUDED.utilization, wait_ratio = EXCLUDED.wait_ratio, idle_ratio = EXCLUDED.idle_ratio,
            wait_count = EXCLUDED.wait_count, wait_ms_avg = EXCLUDED.wait_ms_avg,
            throughput_per_hour = EXCLUDED.throughput_per_hour, lead_time_p50 = EXCLUDED.lead_time_p50,
            lead_time_p90 = EXCLUDED.lead_time_p90, lead_time_p99 = EXCLUDED.lead_time_p99,
            path_efficiency = EXCLUDED.path_efficiency, task_failed_ratio = EXCLUDED.task_failed_ratio,
            conflict_count = EXCLUDED.conflict_count, deadlock_count = EXCLUDED.deadlock_count,
            deadlock_resolve_ms_avg = EXCLUDED.deadlock_resolve_ms_avg, replan_count = EXCLUDED.replan_count,
            replan_per_task = EXCLUDED.replan_per_task, tick_overruns = EXCLUDED.tick_overruns, stall_ms = EXCLUDED.stall_ms,
            computed_at = EXCLUDED.computed_at
        """;

    public Task<CategoryCount> TaskAsync(NpgsqlConnection conn, CancellationToken ct) =>
        CountAsync(conn, "s.category = 'task'", TaskFound, sample: true, ct);

    public Task<CategoryCount> ConflictAsync(NpgsqlConnection conn, CancellationToken ct) =>
        CountAsync(conn, "s.category = 'conflict' AND s.event_type <> 'PathReplanned'", ConflictFound, sample: true, ct);

    public Task<CategoryCount> TelemetryAsync(NpgsqlConnection conn, CancellationToken ct) =>
        CountAsync(conn, "s.category = 'telemetry'", TelemetryFound, sample: false, ct);

    public async Task<long> InvalidTransitionsAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM robot_status_log WHERE run_id IN (SELECT DISTINCT run_id FROM sent) AND NOT valid", conn);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<IReadOnlyList<string>> RunIdsAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT DISTINCT run_id FROM sent ORDER BY run_id", conn);
        var runs = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            runs.Add(reader.GetString(0));
        return runs;
    }

    public async Task ComputeRunMetricsAsync(NpgsqlConnection conn, string runId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(RunMetricsSql, conn);
        cmd.Parameters.AddWithValue("run", runId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<CategoryCount> CountAsync(NpgsqlConnection conn, string filter, string found, bool sample,
        CancellationToken ct)
    {
        long sent, stored;
        await using (var cmd = new NpgsqlCommand(
            $"SELECT count(*), count(*) FILTER (WHERE {found}) FROM sent s WHERE {filter}", conn))
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            sent = reader.GetInt64(0);
            stored = reader.GetInt64(1);
        }

        var missing = new List<string>();
        if (sample && stored < sent)
        {
            await using var cmd = new NpgsqlCommand(
                $"SELECT s.event_id FROM sent s WHERE {filter} AND NOT ({found}) ORDER BY s.event_id LIMIT {SampleLimit}", conn);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                missing.Add(reader.GetString(0));
        }

        return new CategoryCount(sent, stored, sent - stored, missing);
    }
}
