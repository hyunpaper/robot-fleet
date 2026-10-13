using RobotFleet.Shared.Geometry;
using RobotFleet.Shared.Map;

namespace RobotFleet.FleetController.Arbitration;

/// 회피 지점 BFS. 통과 가능한 칸(통행 가능 + 미점유·미예약, 경로 칸도 미점유 — 시작 칸만 예외)만 확장하고,
/// 그 중 isGoal을 만족하는 가장 가까운 칸을 돌려준다. 순수 함수. 13 §4
public static class YieldSearch
{
    // 방향 순서 N·E·S·W 고정 → 같은 거리면 방문 순서가 결정적(13 §9)
    private static readonly Heading[] Dirs = { Heading.North, Heading.East, Heading.South, Heading.West };

    /// traversable(cell): 지나갈 수 있는 칸(통행 가능 + 미점유). isGoal(cell): 양보해 멈출 수 있는 칸(통행 가능·미점유 + 회피 조건).
    /// 반환: (회피 지점, 탐색 홉). 못 찾으면 (null, maxDistance). 시작 칸은 로봇이 서 있으므로 traversable 검사 없이 출발한다
    public static (Point? YieldPoint, int Searched) Find(
        GridMap map, Point from, int maxDistance, Func<Point, bool> traversable, Func<Point, bool> isGoal)
    {
        var visited = new HashSet<Point> { from };
        var queue = new Queue<(Point Cell, int Dist)>();
        queue.Enqueue((from, 0));
        while (queue.Count > 0)
        {
            var (cell, dist) = queue.Dequeue();
            if (dist > 0 && isGoal(cell)) return (cell, dist);
            if (dist >= maxDistance) continue;

            foreach (var dir in Dirs)
            {
                var nb = cell.Step(dir);
                if (!visited.Add(nb)) continue;
                if (!map.InBounds(nb) || !traversable(nb)) continue;   // 경로 칸은 미점유만 — 통과 불가면 버린다
                queue.Enqueue((nb, dist + 1));
            }
        }

        return (null, maxDistance);
    }
}
