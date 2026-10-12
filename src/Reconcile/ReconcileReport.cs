namespace RobotFleet.Reconcile;

public sealed record CategoryCount(long Sent, long Stored, long Missing, IReadOnlyList<string> MissingSample);

/// 종료 코드: Task · Conflict Missing 0 → 0
public sealed record ReconcileReport(string MapId, CategoryCount Task, CategoryCount Conflict,
    CategoryCount Telemetry, long InvalidTransitions);
