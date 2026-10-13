namespace RobotFleet.FleetController.Arbitration;

/// 04 설정값 9~13(교착). 기본값·허용 범위·연동 제약은 04 "## 설정값". 13 §8
public sealed record DeadlockOptions
{
    public int DeadlockDetectWaitMs { get; init; } = 1000;      // 9
    public int YieldSearchMaxDistance { get; init; } = 5;       // 10
    public double YieldDistanceWeight { get; init; } = 1.0;     // 11
    public double YieldPriorityWeight { get; init; } = 1.0;     // 12
    public int DeadlockResolveTtlTicks { get; init; } = 50;     // 13

    /// 04 허용 범위 + 연동 제약(TTL >= 탐색 거리 × 10). 어기면 ArgumentException
    public void Validate()
    {
        Range(nameof(DeadlockDetectWaitMs), DeadlockDetectWaitMs, 500, 3000);
        Range(nameof(YieldSearchMaxDistance), YieldSearchMaxDistance, 3, 15);
        Range(nameof(YieldDistanceWeight), YieldDistanceWeight, 0, 3.0);
        Range(nameof(YieldPriorityWeight), YieldPriorityWeight, 0, 3.0);
        Range(nameof(DeadlockResolveTtlTicks), DeadlockResolveTtlTicks, 30, 120);
        if (DeadlockResolveTtlTicks < YieldSearchMaxDistance * 10)
        {
            throw new ArgumentException(
                $"deadlock_resolve_ttl_ticks({DeadlockResolveTtlTicks})는 yield_search_max_distance({YieldSearchMaxDistance}) × 10 이상이어야 한다");
        }
    }

    private static void Range(string name, double value, double min, double max)
    {
        if (value < min || value > max)
            throw new ArgumentException($"{name}({value})는 {min}~{max} 범위여야 한다(04 허용 범위)");
    }
}
