using RobotFleet.Shared.Geometry;

namespace RobotFleet.FleetController.Arbitration;

public interface IMoveArbiter
{
    /// <summary>한 틱의 이동 요청 전부. 01의 배치 API와 1:1. 응답 순서는 요청 순서와 같다.</summary>
    ValueTask<IReadOnlyList<MoveDecision>> DecideAsync(
        string mapId, long tick, IReadOnlyList<MoveRequest> requests, CancellationToken ct = default);

    /// <summary>이동 완료 후 이전 칸 해제. M1 구현은 no-op.</summary>
    ValueTask ReleaseAsync(string mapId, string robotId, Point cell, CancellationToken ct = default);
}
