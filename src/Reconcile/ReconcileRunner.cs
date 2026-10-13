using System.Text.Json;
using Npgsql;

namespace RobotFleet.Reconcile;

public sealed class ReconcileRunner(IReconcileQueries queries, TextWriter output)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// 적체 0 대기는 하지 않는다. 연결 → 임시 테이블 적재 → 차집합 질의 → (누락 0이면) run_metrics → 보고서 출력. 종료 코드 반환
    public async Task<int> RunAsync(ReconcileOptions options, string connectionString, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await SentEventImporter.ImportAsync(conn, SendLogReader.Read(options.SendLogPath), ct);

        var runIds = await queries.RunIdsAsync(conn, ct);
        var task = await queries.TaskAsync(conn, ct);
        var conflict = await queries.ConflictAsync(conn, ct);
        var telemetry = await queries.TelemetryAsync(conn, ct);
        var invalid = await queries.InvalidTransitionsAsync(conn, ct);

        // run 집계는 적재 완료가 확인된 뒤에만(SimulationEnded 직후엔 다른 파티션이 아직 적재 중일 수 있다, 01 · 10 §6)
        var computed = new List<string>();
        if (task.Missing == 0 && conflict.Missing == 0)
        {
            foreach (var runId in runIds)
            {
                await queries.ComputeRunMetricsAsync(conn, runId, ct);
                computed.Add(runId);
            }
        }

        var report = new ReconcileReport(options.MapId, runIds, task, conflict, telemetry, invalid, computed);

        var json = JsonSerializer.Serialize(report, JsonOptions);
        await output.WriteLineAsync(json);
        if (options.OutPath is not null)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(options.OutPath));
            if (directory is not null)
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(options.OutPath, json, ct);
        }

        return report.Task.Missing == 0 && report.Conflict.Missing == 0 ? 0 : 1;
    }
}
