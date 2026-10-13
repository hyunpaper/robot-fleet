using RobotFleet.FleetController.Arbitration;
using RobotFleet.Shared.Domain;
using RobotFleet.Shared.Geometry;

namespace RobotFleet.Simulator;

/// RobotStepper 관찰 지점(10 §1). 판정 · 동작에는 영향을 주지 않는다
public interface IRobotObserver
{
    void OnStatusChanged(Robot robot, RobotStatus from, RobotStatus to, long tick);

    /// 이동 거부. target = 들어가려던 칸
    void OnDenied(Robot robot, MoveDecision decision, Point target, long tick);

    /// 판정 응답에 실린 교착 해소(13 §2). 반환 = 이번 틱 결정을 건너뛸 로봇들(양보로 경로를 새로 받은 로봇·실패로 Error가 된 순환 로봇)
    IReadOnlyCollection<string> OnDeadlock(DeadlockInfo info, long tick);
}
