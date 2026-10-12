using System.Diagnostics;
using RobotFleet.Shared.Geometry;
using RobotFleet.Shared.Map;

namespace RobotFleet.FleetController.Planning;

/// <summary>4방향 A*. 노드 = (x, y, heading), 회전 비용 포함. 같은 입력이면 항상 같은 경로를 낸다.</summary>
public sealed class AStarPathPlanner : IPathPlanner
{
    // 확장 순서 N, E, S, W (Heading 값 순서와 같다)
    private static readonly int[] Dx = { 0, 1, 0, -1 };
    private static readonly int[] Dy = { -1, 0, 1, 0 };

    private readonly PathCost _cost;
    private readonly double[] _turnCost = new double[16];   // [from * 4 + to]

    public AStarPathPlanner(PathCost? cost = null)
    {
        _cost = cost ?? PathCost.Default;
        for (var from = 0; from < 4; from++)
            for (var to = 0; to < 4; to++)
                _turnCost[from * 4 + to] = _cost.Turn((Heading)from, (Heading)to);
    }

    public PathResult FindPath(GridMap map, Point start, Heading startHeading, Point goal)
    {
        var startedAt = Stopwatch.GetTimestamp();

        if (!map.IsWalkable(start)) return PathResult.Failure(PathStatus.InvalidStart, 0, Stopwatch.GetElapsedTime(startedAt));
        if (!map.IsWalkable(goal)) return PathResult.Failure(PathStatus.InvalidGoal, 0, Stopwatch.GetElapsedTime(startedAt));
        if (start == goal)
        {
            return new PathResult
            {
                Status = PathStatus.AlreadyAtGoal,
                Steps = new[] { new PathStep(start, startHeading) },
                Cost = 0,
                ExpandedNodes = 0,
                Elapsed = Stopwatch.GetElapsedTime(startedAt),
            };
        }

        // 노드 id = (y * Width + x) * 4 + heading
        var width = map.Width;
        var nodeCount = width * map.Height * 4;
        var g = new double[nodeCount];
        var parent = new int[nodeCount];
        var closed = new bool[nodeCount];
        Array.Fill(g, double.PositiveInfinity);

        // 동점은 h 작은 쪽(목표에 더 가까운 쪽) → 삽입 순서. seq가 유일해서 완전 순서가 된다
        var open = new PriorityQueue<int, (double F, double H, long Seq)>();
        long seq = 0;

        var startId = (start.Y * width + start.X) * 4 + (int)startHeading;
        g[startId] = 0;
        parent[startId] = -1;
        var startH = Heuristic(start.X, start.Y, goal);
        open.Enqueue(startId, (startH, startH, seq++));

        var expanded = 0;
        while (open.TryDequeue(out var id, out _))
        {
            if (closed[id]) continue;   // 더 싼 값으로 다시 넣어진 노드의 낡은 항목
            closed[id] = true;
            expanded++;

            var cell = id >> 2;
            var h = id & 3;
            var x = cell % width;
            var y = cell / width;
            if (x == goal.X && y == goal.Y)
                return Found(parent, id, g[id], width, expanded, startedAt);   // 도착 방향은 묻지 않는다

            for (var d = 0; d < 4; d++)
            {
                var nx = x + Dx[d];
                var ny = y + Dy[d];
                if (!map.IsWalkable(new Point(nx, ny))) continue;

                var nid = (ny * width + nx) * 4 + d;
                if (closed[nid]) continue;
                var ng = g[id] + _turnCost[h * 4 + d] + _cost.Move;
                if (ng >= g[nid]) continue;

                g[nid] = ng;
                parent[nid] = id;
                var nh = Heuristic(nx, ny, goal);
                open.Enqueue(nid, (ng + nh, nh, seq++));
            }
        }

        return PathResult.Failure(PathStatus.NoPath, expanded, Stopwatch.GetElapsedTime(startedAt));
    }

    // 회전 비용 ≥ 0이므로 맨해튼 × Move가 하한(admissible)이고 consistent
    private double Heuristic(int x, int y, Point goal) => (Math.Abs(x - goal.X) + Math.Abs(y - goal.Y)) * _cost.Move;

    // parent[]를 goal에서 거슬러 올라가 뒤집는다. 시작 노드가 (start, startHeading)이므로 Steps[0].Heading == startHeading
    private static PathResult Found(int[] parent, int goalId, double cost, int width, int expanded, long startedAt)
    {
        var count = 0;
        for (var id = goalId; id >= 0; id = parent[id]) count++;

        var steps = new PathStep[count];
        var i = count;
        for (var id = goalId; id >= 0; id = parent[id])
        {
            var cell = id >> 2;
            steps[--i] = new PathStep(new Point(cell % width, cell / width), (Heading)(id & 3));
        }

        return new PathResult
        {
            Status = PathStatus.Found,
            Steps = steps,
            Cost = cost,
            ExpandedNodes = expanded,
            Elapsed = Stopwatch.GetElapsedTime(startedAt),
        };
    }
}
