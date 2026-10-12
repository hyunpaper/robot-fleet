using RobotFleet.Shared.Geometry;

namespace RobotFleet.FleetController.Arbitration;

/// <summary>M1용. 모든 이동을 승인하고 해제는 하지 않는다.</summary>
public sealed class AlwaysGrantMoveArbiter : IMoveArbiter
{
    public ValueTask<IReadOnlyList<MoveDecision>> DecideAsync(
        string mapId, long tick, IReadOnlyList<MoveRequest> requests, CancellationToken ct = default)
    {
        var decisions = new MoveDecision[requests.Count];
        for (var i = 0; i < decisions.Length; i++)
            decisions[i] = MoveDecision.Grant(requests[i].RobotId);
        return ValueTask.FromResult<IReadOnlyList<MoveDecision>>(decisions);
    }

    public ValueTask ReleaseAsync(string mapId, string robotId, Point cell, CancellationToken ct = default)
        => ValueTask.CompletedTask;
}
