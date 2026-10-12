using Npgsql;

namespace RobotFleet.Reconcile;

/// 송신 집합(임시 테이블 sent) − 적재 집합. 10 §10
public sealed class ReconcileQueries : IReconcileQueries
{
    private const int SampleLimit = 100;

    // task 범주: Task* → task_event_log, RobotStatusChanged → robot_status_log(valid 무관 — 위반도 영수증이 있다)
    private const string TaskFound = """
        EXISTS (SELECT 1 FROM task_event_log t WHERE t.event_id = s.event_id)
        OR EXISTS (SELECT 1 FROM robot_status_log r WHERE r.event_id = s.event_id)
        """;

    private const string ConflictFound = "EXISTS (SELECT 1 FROM conflict_log c WHERE c.event_id = s.event_id)";

    private const string TelemetryFound = """
        EXISTS (SELECT 1 FROM robot_telemetry r WHERE r.map_id = @map AND r.robot_id = s.key AND r.tick = s.tick)
        """;

    public Task<CategoryCount> TaskAsync(NpgsqlConnection conn, string mapId, CancellationToken ct) =>
        CountAsync(conn, mapId, "s.category = 'task'", TaskFound, sample: true, ct);

    public Task<CategoryCount> ConflictAsync(NpgsqlConnection conn, string mapId, CancellationToken ct) =>
        CountAsync(conn, mapId, "s.category = 'conflict' AND s.event_type <> 'PathReplanned'", ConflictFound, sample: true, ct);

    public Task<CategoryCount> TelemetryAsync(NpgsqlConnection conn, string mapId, CancellationToken ct) =>
        CountAsync(conn, mapId, "s.category = 'telemetry'", TelemetryFound, sample: false, ct);

    public async Task<long> InvalidTransitionsAsync(NpgsqlConnection conn, string mapId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM robot_status_log WHERE map_id = @map AND NOT valid", conn);
        cmd.Parameters.AddWithValue("map", mapId);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static async Task<CategoryCount> CountAsync(NpgsqlConnection conn, string mapId, string filter, string found, bool sample,
        CancellationToken ct)
    {
        long sent, stored;
        await using (var cmd = new NpgsqlCommand(
            $"SELECT count(*), count(*) FILTER (WHERE {found}) FROM sent s WHERE {filter}", conn))
        {
            cmd.Parameters.AddWithValue("map", mapId);
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
            cmd.Parameters.AddWithValue("map", mapId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                missing.Add(reader.GetString(0));
        }

        return new CategoryCount(sent, stored, sent - stored, missing);
    }
}
