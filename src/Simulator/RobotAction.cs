using RobotFleet.Shared.Geometry;

namespace RobotFleet.Simulator;

public enum RobotActionKind : byte { None = 0, Turn = 1, Move = 2 }

public sealed class RobotAction
{
    public RobotActionKind Kind { get; init; }
    public Heading TargetHeading { get; init; }   // Turn
    public Point TargetCell { get; init; }        // Move
    public int RemainingMs { get; set; }
}
