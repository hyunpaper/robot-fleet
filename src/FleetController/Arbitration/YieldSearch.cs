using RobotFleet.Shared.Geometry;
using RobotFleet.Shared.Map;

namespace RobotFleet.FleetController.Arbitration;

/// 회피 지점 BFS(통행 가능 + 미점유·미예약, 경로 칸도 미점유 — 시작 칸만 예외). 순수 함수. 13 §4
public static class YieldSearch
{
    /// reserved(cell): 점유·예약 칸이면 true. 반환: (회피 지점, 실제 탐색 홉). 못 찾으면 null·마지막 상한. 본문은 2단계 [핵심]
    public static (Point? YieldPoint, int Searched) Find(GridMap map, Point from, int maxDistance, Func<Point, bool> reserved)
        => throw new NotImplementedException();
}
