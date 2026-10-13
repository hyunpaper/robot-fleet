using Npgsql;

namespace RobotFleet.Reconcile;

/// 모든 질의는 임시 테이블 sent의 run_id로 범위를 정한다(run 경계, 10 §10)
public interface IReconcileQueries
{
    Task<CategoryCount> TaskAsync(NpgsqlConnection conn, CancellationToken ct);

    Task<CategoryCount> ConflictAsync(NpgsqlConnection conn, CancellationToken ct);

    Task<CategoryCount> TelemetryAsync(NpgsqlConnection conn, CancellationToken ct);

    Task<long> InvalidTransitionsAsync(NpgsqlConnection conn, CancellationToken ct);

    /// sent에 나온 run 목록(이름 순)
    Task<IReadOnlyList<string>> RunIdsAsync(NpgsqlConnection conn, CancellationToken ct);

    /// run_metrics 1행 UPSERT. 차집합 0 확인 뒤에만 부른다(01 · 10 §6)
    Task ComputeRunMetricsAsync(NpgsqlConnection conn, string runId, CancellationToken ct);
}
