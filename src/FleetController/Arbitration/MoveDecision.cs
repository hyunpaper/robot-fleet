using RobotFleet.Shared.Geometry;

namespace RobotFleet.FleetController.Arbitration;

public enum MoveDenyReason : byte { None = 0, Occupied = 1, Swap = 2, Deadlock = 3, StateLost = 4 }

public readonly record struct MoveDecision(
    string RobotId, bool Granted, MoveDenyReason Reason = MoveDenyReason.None,
    string? BlockedBy = null, Point? YieldTo = null)
{
    public static MoveDecision Grant(string robotId) => new(robotId, true);
}
