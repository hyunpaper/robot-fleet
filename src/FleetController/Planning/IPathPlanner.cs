using RobotFleet.Shared.Geometry;
using RobotFleet.Shared.Map;

namespace RobotFleet.FleetController.Planning;

public interface IPathPlanner
{
    /// blocked = 일시 장애물로 피할 칸(13 §6 재계획 — 막은 로봇 칸). null이면 맵 통행 가능성만 본다
    PathResult FindPath(GridMap map, Point start, Heading startHeading, Point goal, IReadOnlySet<Point>? blocked = null);
}
