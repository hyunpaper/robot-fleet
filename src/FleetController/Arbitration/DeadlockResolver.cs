namespace RobotFleet.FleetController.Arbitration;

/// 판정 끝에서 교착을 탐지·해소한다(DeadlockGraph + YieldSearch + 해소 락 조합). 상태(락·양보 표시)를 보유한다.
/// InMemoryMoveArbiter가 DecideAsync 판정 직후 호출한다(별도 호출 아님, 13 §2). 세부 시그니처·본문은 2단계 [핵심]
public sealed class DeadlockResolver
{
    public DeadlockResolver(DeadlockOptions options) => Options = options;

    public DeadlockOptions Options { get; }
}
