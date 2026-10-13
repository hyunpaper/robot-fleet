using RobotFleet.Shared.Geometry;

namespace RobotFleet.FleetController.Arbitration;

public enum MoveDenyReason : byte { None = 0, Occupied = 1, Swap = 2, Deadlock = 3, StateLost = 4 }

/// 교착 해소 진행 단계. Detected = 양보 시작(아직 미발행), Resolved = 양보 완료(발행), Failed = 실패(발행). 13 §2
public enum DeadlockPhase : byte { None = 0, Detected = 1, Resolved = 2, Failed = 3 }

/// 배치 판정 응답에 실리는 교착 정보(13 §2). 한 틱의 MoveDecision 하나(탐지 시작 로봇 또는 양보 로봇)에 붙는다.
/// IsNew = 이번에 처음 나온 결정·완료·실패(재전송·재사용은 false) → Simulator 중복 발행 방지(08 §4)
public readonly record struct DeadlockInfo(
    IReadOnlyList<string> Cycle, IReadOnlyList<Point> Cells, string Resolution,
    string? YieldRobotId, Point? YieldTo, int SearchedDistance,
    long DetectTick, int ResolvedMs, DeadlockPhase Phase, bool IsNew);

public readonly record struct MoveDecision(
    string RobotId, bool Granted, MoveDenyReason Reason = MoveDenyReason.None,
    string? BlockedBy = null, Point? YieldTo = null, DeadlockInfo? Deadlock = null)
{
    public static MoveDecision Grant(string robotId) => new(robotId, true);
}
