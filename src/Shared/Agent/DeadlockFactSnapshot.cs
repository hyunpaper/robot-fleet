using RobotFleet.Shared.Geometry;
using RobotFleet.Shared.Tuning;

namespace RobotFleet.Shared.Agent;

/// 04 M9 1단계 산출(규칙이 만든 사실). 모든 사실 항목의 Id는 스냅샷 안에서 유일하다(검증 0의 대조 키). 14 §2.2
public sealed record DeadlockFactSnapshot(
    string SnapshotId,
    DeadlockCauseType CauseType,
    IReadOnlyList<string> RunIds,
    IReadOnlyList<CycleFact> Cycles,
    IReadOnlyList<CellFact> Cells,
    IReadOnlyList<SettingFact> Settings,
    IReadOnlyList<MetricFact> Metrics,
    IReadOnlyList<PastActionFact> PastActions);

/// 04 cause_type 6종
public enum DeadlockCauseType
{
    CycleWait = 1,
    SwapHeadon = 2,
    FollowingDelay = 3,
    ReplanStorm = 4,
    Starvation = 5,
    NoYieldPoint = 6,
}

public sealed record CycleFact(
    string Id, IReadOnlyList<string> RobotIds, IReadOnlyList<Point> Cells,
    int Occurrences, long MaxWaitMs, int NoYieldRetries);

/// 05: 혼잡도 = 대기 발생 / 통행량
public sealed record CellFact(string Id, int X, int Y, long Passes, long WaitEvents, double Congestion);

/// 13개 키가 정확히 한 번씩 들어온다(조립 시 확인)
public sealed record SettingFact(string Id, TuningKey Key, double CurrentValue);

public sealed record MetricFact(string Id, string RunId, RunMetric Metric, double Value, string Unit);

public sealed record PastActionFact(
    string Id, string ProposalId, TuningKey Key, double OldValue, double NewValue,
    string AppliedFromRunId, PastActionOutcome Outcome);

/// 04 "영향 지표" + 검증 6 지표(05 정의)
public enum RunMetric
{
    ThroughputPerHour = 1,
    DeadlockRatePerHour = 2,
    DeadlockResolveMsP99 = 3,
    LeadTimeMsP50 = 4,
    LeadTimeMsP90 = 5,
    LeadTimeMsP99 = 6,
    CongestionLossRatio = 7,
    PathEfficiency = 8,
    TaskFailureRate = 9,
    ReplanRate = 10,
    ConflictRate = 11,
    DecisionLatencyMsP99 = 12,
}

public enum PastActionOutcome
{
    AppliedImproved = 1,
    AppliedNotImproved = 2,
    ValidationFailed = 3,
    Rejected = 4,
}
