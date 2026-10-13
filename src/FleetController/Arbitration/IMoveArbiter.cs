using RobotFleet.Shared.Geometry;

namespace RobotFleet.FleetController.Arbitration;

public interface IMoveArbiter
{
    /// <summary>한 틱의 이동 요청 전부. 01의 배치 API와 1:1. 응답 순서는 요청 순서와 같다.</summary>
    ValueTask<IReadOnlyList<MoveDecision>> DecideAsync(
        string mapId, long tick, IReadOnlyList<MoveRequest> requests, CancellationToken ct = default);

    /// <summary>이동 완료 후 이전 칸 해제. M1 구현은 no-op.</summary>
    ValueTask ReleaseAsync(string mapId, string robotId, Point cell, CancellationToken ct = default);

    /// <summary>
    /// run 종료 마무리: 아직 해소되지 않은 교착을 failed(resolvedMs=경과)로 확정해 돌려준다. 멱등 — 재호출 시 같은 목록(13 §2).
    /// 중재기가 스스로 끝내는 것이지 Simulator의 해제가 아니다. run 도중 호출 금지.
    /// </summary>
    IReadOnlyList<DeadlockInfo> DrainPendingDeadlocks(string mapId, long tick);
}
