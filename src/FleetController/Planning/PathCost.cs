using RobotFleet.Shared.Geometry;
using RobotFleet.Shared.Tuning;

namespace RobotFleet.FleetController.Planning;

public sealed record PathCost(double Move = 1.0, double TurnPer90 = TuningDefaults.TurnCost)
{
    public static readonly PathCost Default = new();

    /// <summary>from을 보고 있다가 to 방향으로 돌리는 비용. 180도 = 2 × TurnPer90 = 1.0</summary>
    public double Turn(Heading from, Heading to) => from.TurnSteps(to) * TurnPer90;
}
