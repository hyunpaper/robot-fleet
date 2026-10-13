namespace RobotFleet.FleetController.Arbitration;

/// 대기 그래프 순환 탐지(함수형, out-degree ≤ 1 → 서로소 순환). 순수 함수. 13 §3
public static class DeadlockGraph
{
    /// waitEdges: robotId → blockedBy(이번 틱 거부만). waitMs: robotId → 누적 대기ms.
    /// 반환: 서로소 순환들(각 순환은 thresholdMs 이상 대기 로봇을 하나 이상 포함), 결정적 순서. 본문은 2단계 [핵심]
    public static IReadOnlyList<IReadOnlyList<string>> FindCycles(
        IReadOnlyDictionary<string, string> waitEdges, IReadOnlyDictionary<string, int> waitMs, int thresholdMs)
        => throw new NotImplementedException();
}
