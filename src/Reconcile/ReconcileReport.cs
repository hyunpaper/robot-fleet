namespace RobotFleet.Reconcile;

public sealed record CategoryCount(long Sent, long Stored, long Missing, IReadOnlyList<string> MissingSample);

/// 종료 코드: Task · Conflict Missing 0 → 0. RunMetricsComputed = run_metrics를 갱신한 run(차집합 0일 때만)
public sealed record ReconcileReport(string MapId, IReadOnlyList<string> RunIds, CategoryCount Task, CategoryCount Conflict,
    CategoryCount Telemetry, long InvalidTransitions, IReadOnlyList<string> RunMetricsComputed);
