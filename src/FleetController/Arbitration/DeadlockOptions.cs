namespace RobotFleet.FleetController.Arbitration;

/// 04 설정값 9~13(교착). 기본값·허용 범위·연동 제약은 04 "## 설정값". 13 §8
public sealed record DeadlockOptions
{
    public int DeadlockDetectWaitMs { get; init; } = 1000;      // 9
    public int YieldSearchMaxDistance { get; init; } = 5;       // 10
    public double YieldDistanceWeight { get; init; } = 1.0;     // 11
    public double YieldPriorityWeight { get; init; } = 1.0;     // 12
    public int DeadlockResolveTtlTicks { get; init; } = 50;     // 13

    /// 연동 제약: TTL >= 탐색 거리 × 10 (04). 어기면 ArgumentException. 본문은 2단계 S1
    public void Validate() => throw new NotImplementedException();
}
