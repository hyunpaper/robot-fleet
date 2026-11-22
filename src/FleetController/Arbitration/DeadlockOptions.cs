using RobotFleet.Shared.Tuning;

namespace RobotFleet.FleetController.Arbitration;

/// 04 설정값 9~13(교착). 기본값·허용 범위·연동 제약은 04 "## 설정값". 13 §8
public sealed record DeadlockOptions
{
    public int DeadlockDetectWaitMs { get; init; } = TuningDefaults.DeadlockDetectWaitMs;       // 9
    public int YieldSearchMaxDistance { get; init; } = TuningDefaults.YieldSearchMaxDistance;   // 10
    public double YieldDistanceWeight { get; init; } = TuningDefaults.YieldCostDistanceWeight;  // 11
    public double YieldPriorityWeight { get; init; } = TuningDefaults.YieldCostPriorityWeight;  // 12
    public int DeadlockResolveTtlTicks { get; init; } = TuningDefaults.DeadlockResolveTtlTicks; // 13

    /// 04 허용 범위 + 연동 제약(TTL >= 탐색 거리 × 10). 어기면 ArgumentException
    public void Validate()
    {
        TuningCatalog.ValidateRange(TuningKey.DeadlockDetectWaitMs, DeadlockDetectWaitMs);
        TuningCatalog.ValidateRange(TuningKey.YieldSearchMaxDistance, YieldSearchMaxDistance);
        TuningCatalog.ValidateRange(TuningKey.YieldCostDistanceWeight, YieldDistanceWeight);
        TuningCatalog.ValidateRange(TuningKey.YieldCostPriorityWeight, YieldPriorityWeight);
        TuningCatalog.ValidateRange(TuningKey.DeadlockResolveTtlTicks, DeadlockResolveTtlTicks);
        TuningCatalog.ValidateCouplings(k => k switch
        {
            TuningKey.YieldSearchMaxDistance => YieldSearchMaxDistance,
            TuningKey.DeadlockResolveTtlTicks => DeadlockResolveTtlTicks,
            _ => TuningCatalog.Get(k).Default,
        });
    }
}
