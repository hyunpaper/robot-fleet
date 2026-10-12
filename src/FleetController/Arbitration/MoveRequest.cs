using RobotFleet.Shared.Geometry;

namespace RobotFleet.FleetController.Arbitration;

public readonly record struct MoveRequest(
    string RobotId, Point From, Point To, Heading Heading, long Tick,
    int WaitMs = 0, int RemainingSteps = 0, int TaskPriority = 0);   // 뒤 셋은 01 배치 API의 우선순위 입력
