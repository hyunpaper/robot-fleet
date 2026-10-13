using RobotFleet.Shared.Geometry;

namespace RobotFleet.FleetController.Arbitration;

public enum MoveDenyReason : byte { None = 0, Occupied = 1, Swap = 2, Deadlock = 3, StateLost = 4 }

/// 교착 해소 진행 단계. Detected = 양보 경로 심기 지시(미발행), YieldExpired = 양보 TTL 만료 — 양보 표시 해제 지시(미발행).
/// 나머지는 에피소드 종료 때 1건 발행(13 §5): Resolved = 양보 완료, Reroute = 순환 소멸(구성원이 대안으로 빠짐), Failed = run 종료 시 미해소
public enum DeadlockPhase : byte { None = 0, Detected = 1, Resolved = 2, Reroute = 3, Failed = 4, YieldExpired = 5 }

/// 배치 판정 응답에 실리는 교착 정보(13 §2 · §5). 한 에피소드(같은 cycleKey)당 종료 때 1건 발행한다.
/// IsNew = 이번에 처음 나온 지시·종료(재전송·재사용은 false) → Simulator 중복 발행 방지(08 §4).
/// NoYieldRetries = 회피 지점을 못 찾아 재시도한 횟수(M9 no_yield_point 판정)
public readonly record struct DeadlockInfo(
    IReadOnlyList<string> Cycle, IReadOnlyList<Point> Cells, string Resolution,
    string? YieldRobotId, Point? YieldTo, int SearchedDistance,
    long DetectTick, int ResolvedMs, DeadlockPhase Phase, bool IsNew, int NoYieldRetries);

public readonly record struct MoveDecision(
    string RobotId, bool Granted, MoveDenyReason Reason = MoveDenyReason.None,
    string? BlockedBy = null, Point? YieldTo = null, DeadlockInfo? Deadlock = null)
{
    public static MoveDecision Grant(string robotId) => new(robotId, true);
}
