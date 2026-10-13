using RobotFleet.Shared.Geometry;
using RobotFleet.Shared.Map;

namespace RobotFleet.FleetController.Arbitration;

/// 판정 끝에서 교착을 탐지·해소한다(DeadlockGraph + YieldSearch + 해소 락 조합). 해소 락·양보 표시 상태를 보유한다.
/// InMemoryMoveArbiter가 DecideAsync 판정 직후 호출한다(별도 호출 아님, 13 §2 · §5). 단일 프로세스라 외부 동기화 없음
public sealed class DeadlockResolver
{
    private readonly DeadlockOptions _options;
    private readonly int _tickMs;
    private readonly Dictionary<string, Lock> _locks = new(StringComparer.Ordinal);
    private IReadOnlyList<DeadlockInfo>? _drained;   // run 종료 드레인 멱등 캐시

    public DeadlockResolver(DeadlockOptions options, int tickMs)
    {
        _options = options;
        _tickMs = tickMs;
    }

    public DeadlockOptions Options => _options;

    /// 이번 틱 거부에서 순환을 찾아 새 순환마다 양보/실패를 결정한다. 이미 락이 있는 순환은 재결정하지 않는다(13 §3 · §5)
    public IReadOnlyList<DeadlockInfo> Resolve(
        long tick,
        IReadOnlyDictionary<string, string> waitEdges,
        IReadOnlyDictionary<string, int> waitMs,
        IReadOnlyDictionary<string, int> taskPriority,
        IReadOnlyDictionary<string, Point> positionOf,
        GridMap map,
        Func<Point, bool> reserved)
    {
        var cycles = DeadlockGraph.FindCycles(waitEdges, waitMs, _options.DeadlockDetectWaitMs);
        if (cycles.Count == 0) return Array.Empty<DeadlockInfo>();

        var marked = new HashSet<string>(_locks.Values.Where(l => l.YieldInProgress).Select(l => l.YieldRobotId!), StringComparer.Ordinal);
        var results = new List<DeadlockInfo>();
        foreach (var cycle in cycles)
        {
            var key = Key(cycle);
            if (_locks.ContainsKey(key)) continue;                 // 해소 중·실패 쿨다운 — 재결정 안 함
            if (cycle.Any(marked.Contains)) continue;              // 양보 표시된 로봇이 낀 순환은 건너뜀

            var cells = cycle.Select(r => positionOf[r]).ToList();
            var (yieldRobot, yieldTo, searched) = PickYield(cycle, waitEdges, taskPriority, positionOf, map, reserved);
            Lock entry;
            if (yieldRobot is null)
            {
                entry = new Lock(cycle, cells, tick, "failed", null, null, searched, tick + _options.DeadlockResolveTtlTicks, false);
                _locks[key] = entry;
                results.Add(Info(entry, DeadlockPhase.Failed, "failed", 0, true));
            }
            else
            {
                entry = new Lock(cycle, cells, tick, "yield", yieldRobot, yieldTo, searched, tick + _options.DeadlockResolveTtlTicks, true);
                _locks[key] = entry;
                marked.Add(yieldRobot);
                results.Add(Info(entry, DeadlockPhase.Detected, "yield", 0, true));
            }
        }

        return results;
    }

    /// 양보 완료(점유로 감지)·TTL 만료를 매 틱 확인한다(13 §5). 완료는 락 해제, TTL 만료는 failed로 쿨다운 전환
    public IReadOnlyList<DeadlockInfo> DetectCompletions(long tick, IReadOnlyDictionary<string, Point> positionOf)
    {
        var results = new List<DeadlockInfo>();
        foreach (var (key, entry) in _locks.ToList())
        {
            if (entry.YieldInProgress)
            {
                if (positionOf.TryGetValue(entry.YieldRobotId!, out var pos) && pos == entry.YieldTo!.Value)
                {
                    results.Add(Info(entry, DeadlockPhase.Resolved, "yield", ElapsedMs(entry, tick), true));
                    _locks.Remove(key);                            // 해소 완료 → 같은 로봇 재교착 가능
                }
                else if (tick >= entry.ExpireTick)
                {
                    results.Add(Info(entry, DeadlockPhase.Failed, "failed", ElapsedMs(entry, tick), true));
                    _locks[key] = entry with { YieldInProgress = false, Resolution = "failed", ExpireTick = tick + _options.DeadlockResolveTtlTicks };
                }
            }
            else if (tick >= entry.ExpireTick)
            {
                _locks.Remove(key);                                // 실패 쿨다운 끝 → 재탐지 허용
            }
        }

        return results;
    }

    /// run 종료: 진행 중 교착을 failed로 확정해 돌려준다. 멱등 — 재호출 시 같은 목록(13 §2)
    public IReadOnlyList<DeadlockInfo> DrainPending(long tick)
    {
        if (_drained is not null) return _drained;
        _drained = _locks.Values
            .Where(e => e.YieldInProgress)
            .Select(e => Info(e, DeadlockPhase.Failed, "failed", ElapsedMs(e, tick), true))
            .ToList();
        return _drained;
    }

    // 순환의 각 로봇에 회피 지점을 BFS로 찾아 양보 비용 최소를 고른다. 아무도 못 찾으면 실패(null). 13 §4
    private (string? Robot, Point? YieldTo, int Searched) PickYield(
        IReadOnlyList<string> cycle, IReadOnlyDictionary<string, string> waitEdges, IReadOnlyDictionary<string, int> taskPriority,
        IReadOnlyDictionary<string, Point> positionOf, GridMap map, Func<Point, bool> reserved)
    {
        string? best = null;
        Point? bestPoint = null;
        var bestCost = double.MaxValue;
        var bestSearched = 0;
        var maxSearched = 0;   // 아무도 못 찾으면 SearchedDistance = 가장 멀리 본 상한

        foreach (var robot in cycle)
        {
            var from = positionOf[robot];
            var dir = Delta(from, positionOf[waitEdges[robot]]);   // 막은 로봇 쪽 축
            bool Traversable(Point c) => map.IsWalkable(c) && !reserved(c);
            bool IsGoal(Point c) => Traversable(c) && !OnAxis(c, from, dir);   // 통로 밖(축 벗어난) 빈 칸

            var (point, searched) = YieldSearch.Find(map, from, _options.YieldSearchMaxDistance, Traversable, IsGoal);
            if (point is null)   // 범위 확대 +2 한 번(04 파생 제약)
                (point, searched) = YieldSearch.Find(map, from, _options.YieldSearchMaxDistance + 2, Traversable, IsGoal);

            maxSearched = Math.Max(maxSearched, searched);
            if (point is null) continue;

            var priority = taskPriority.GetValueOrDefault(robot);
            var cost = _options.YieldDistanceWeight * searched + _options.YieldPriorityWeight * (1.0 / Math.Max(priority, 1));
            if (cost < bestCost || (cost == bestCost && string.CompareOrdinal(robot, best) < 0))
            {
                best = robot;
                bestPoint = point;
                bestCost = cost;
                bestSearched = searched;   // 선정된 후보의 탐색 홉
            }
        }

        return best is null ? (null, null, maxSearched) : (best, bestPoint, bestSearched);
    }

    private int ElapsedMs(Lock e, long tick) => (int)((tick - e.DetectTick) * _tickMs);

    private static DeadlockInfo Info(Lock e, DeadlockPhase phase, string resolution, int resolvedMs, bool isNew) =>
        new(e.Cycle, e.Cells, resolution, e.YieldRobotId, e.YieldTo, e.SearchedDistance, e.DetectTick, resolvedMs, phase, isNew);

    // 막은 로봇 쪽으로의 단위 방향(인접하므로 한 축 ±1)
    private static Point Delta(Point from, Point blocker) => new(Math.Sign(blocker.X - from.X), Math.Sign(blocker.Y - from.Y));

    // dir이 가로면 같은 행, 세로면 같은 열이 축(회피 지점이 될 수 없는 직선)
    private static bool OnAxis(Point c, Point from, Point dir) => dir.X != 0 ? c.Y == from.Y : c.X == from.X;

    private static string Key(IReadOnlyList<string> cycle) =>
        string.Join('|', cycle.OrderBy(r => r, StringComparer.Ordinal));

    private sealed record Lock(
        IReadOnlyList<string> Cycle, IReadOnlyList<Point> Cells, long DetectTick, string Resolution,
        string? YieldRobotId, Point? YieldTo, int SearchedDistance, long ExpireTick, bool YieldInProgress);
}
