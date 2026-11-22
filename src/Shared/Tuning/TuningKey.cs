namespace RobotFleet.Shared.Tuning;

/// 04 "## 설정값" 등급 A 13개. 값 = 04 표 번호. 14 §2.1
public enum TuningKey
{
    CongestionAlpha = 1,
    TurnCost = 2,
    PriorityWaitWeight = 3,
    PriorityDistanceWeight = 4,
    PriorityTaskPriorityWeight = 5,
    ReplanWaitThresholdMs = 6,
    ReplanCooldownMs = 7,
    ReplanMaxPerTask = 8,
    DeadlockDetectWaitMs = 9,
    YieldSearchMaxDistance = 10,
    YieldCostDistanceWeight = 11,
    YieldCostPriorityWeight = 12,
    DeadlockResolveTtlTicks = 13,
}

public enum TuningValueKind
{
    Integer = 1,
    Decimal = 2,
}

/// Name = 04 설정 키 원문(`priority.wait_weight`처럼 점 포함). Topic · Metrics = 04 "본문" · "영향 지표" 열
public sealed record TuningParameter(
    TuningKey Key, string Name, TuningValueKind Kind,
    double Default, double Min, double Max, double MaxStep,
    string Topic, string Metrics);

/// Dependent >= Driver × Factor (04 연동 제약)
public sealed record TuningCoupling(TuningKey Dependent, TuningKey Driver, double Factor);
