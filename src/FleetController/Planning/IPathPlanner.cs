using RobotFleet.Shared.Geometry;
using RobotFleet.Shared.Map;

namespace RobotFleet.FleetController.Planning;

public interface IPathPlanner
{
    PathResult FindPath(GridMap map, Point start, Heading startHeading, Point goal);
}
