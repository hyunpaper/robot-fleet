using System.Text.Json;
using Npgsql;

namespace RobotFleet.Reconcile;

public sealed class ReconcileRunner(IReconcileQueries queries, TextWriter output)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// 적체 0 대기는 하지 않는다. 연결 → 임시 테이블 적재 → 질의 4개 → 보고서 출력. 종료 코드 반환
    public async Task<int> RunAsync(ReconcileOptions options, string connectionString, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await SentEventImporter.ImportAsync(conn, SendLogReader.Read(options.SendLogPath), ct);

        var report = new ReconcileReport(
            options.MapId,
            await queries.TaskAsync(conn, options.MapId, ct),
            await queries.ConflictAsync(conn, options.MapId, ct),
            await queries.TelemetryAsync(conn, options.MapId, ct),
            await queries.InvalidTransitionsAsync(conn, options.MapId, ct));

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
