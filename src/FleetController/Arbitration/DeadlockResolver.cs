using RobotFleet.Shared.Geometry;
using RobotFleet.Shared.Map;

namespace RobotFleet.FleetController.Arbitration;

/// 판정 끝에서 교착을 탐지·해소한다(DeadlockGraph + YieldSearch + 에피소드 락). 13 §2 · §5.
/// 에피소드 = 같은 cycleKey의 최초 탐지~종료. 회피 지점이 없어도 작업을 실패시키지 않고 deadlock_detect_wait_ms 간격으로
/// 재시도하며(에피소드 유지), 종료 때(양보 완료=yield / 순환 소멸=reroute / run 종료 미해소=failed) 1건만 발행한다.
/// InMemoryMoveArbiter가 DecideAsync 판정 직후 호출한다(단일 프로세스, 외부 동기화 없음)
public sealed class DeadlockResolver
{
    private readonly DeadlockOptions _options;
    private readonly int _tickMs;
    private readonly int _retryTicks;   // 재시도·순환 소멸 유예 간격 = deadlock_detect_wait_ms
    private readonly Dictionary<string, Episode> _episodes = new(StringComparer.Ordinal);
    private IReadOnlyList<DeadlockInfo>? _drained;   // run 종료 드레인 멱등 캐시

    public DeadlockResolver(DeadlockOptions options, int tickMs)
    {
        _options = options;
        _tickMs = tickMs;
        _retryTicks = Math.Max(1, options.DeadlockDetectWaitMs / tickMs);
    }

    public DeadlockOptions Options => _options;

    /// 지금 양보 이동 중인 로봇인가(판정 점수 선행 버킷용, 13 §4 · §6)
    public bool IsYielding(string robotId) =>
        _episodes.Values.Any(e => string.Equals(e.YieldRobotId, robotId, StringComparison.Ordinal));

    /// 회피 지점을 못 찾아 재시도 중인(장기 미해소) 에피소드 수(메트릭, 13 §5)
    public int UnresolvedCount => _episodes.Values.Count(e => e.NoYieldRetries > 0 && e.YieldRobotId is null);

    /// 그 중 가장 오래된 에피소드의 경과 ms. 없으면 0
    public int OldestUnresolvedMs(long tick)
    {
        var oldest = _episodes.Values.Where(e => e.NoYieldRetries > 0 && e.YieldRobotId is null)
            .Select(e => (int)((tick - e.DetectTick) * _tickMs)).DefaultIfEmpty(0).Max();
        return oldest;
    }

    /// 새 순환 탐지 + 재시도 양보 선정. 반환 = 이번 틱 양보 경로를 심을 지시(Detected). 종료 발행은 DetectCompletions가 한다(13 §5)
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
        var present = cycles.Select(Key).ToHashSet(StringComparer.Ordinal);

        // 재시도 중(양보 아님) 에피소드의 순환 존재 여부 갱신 — 소멸 유예(reroute 판정)용
        foreach (var (key, e) in _episodes)
            if (e.YieldRobotId is null)
                _episodes[key] = e with { AbsentSinceTick = present.Contains(key) ? null : (e.AbsentSinceTick ?? tick) };

        var marked = new HashSet<string>(_episodes.Values.Where(e => e.YieldRobotId is not null).Select(e => e.YieldRobotId!),
            StringComparer.Ordinal);
        var directives = new List<DeadlockInfo>();
        foreach (var cycle in cycles)
        {
            var key = Key(cycle);
            if (cycle.Any(marked.Contains)) continue;   // 양보 표시된 로봇이 낀 순환은 건너뜀

            if (_episodes.TryGetValue(key, out var ep))
            {
                if (ep.YieldRobotId is not null) continue;                 // 양보 진행 중 — 재시도 안 함
                if (tick - ep.LastAttemptTick < _retryTicks) continue;     // 재시도 간격 미도달
                Attempt(key, ep, cycle, tick, waitEdges, taskPriority, positionOf, map, reserved, marked, directives);
            }
            else
            {
                var fresh = new Episode(cycle, cycle.Select(r => positionOf[r]).ToList(), tick, tick, 0, 0, null, null, 0, null);
                Attempt(key, fresh, cycle, tick, waitEdges, taskPriority, positionOf, map, reserved, marked, directives);
            }
        }

        return directives;
    }

    // 한 번의 양보 시도. 회피 지점을 찾으면 양보 시작(Detected 지시), 못 찾으면 noYieldRetries+1로 에피소드 유지(무발행)
    private void Attempt(string key, Episode ep, IReadOnlyList<string> cycle, long tick,
        IReadOnlyDictionary<string, string> waitEdges, IReadOnlyDictionary<string, int> taskPriority,
        IReadOnlyDictionary<string, Point> positionOf, GridMap map, Func<Point, bool> reserved,
        HashSet<string> marked, List<DeadlockInfo> directives)
    {
        var cells = cycle.Select(r => positionOf[r]).ToList();
        var (yieldRobot, yieldTo, searched) = PickYield(cycle, waitEdges, taskPriority, positionOf, map, reserved);
        if (yieldRobot is null)
        {
            _episodes[key] = ep with { Cells = cells, NoYieldRetries = ep.NoYieldRetries + 1, SearchedDistance = searched, LastAttemptTick = tick, AbsentSinceTick = null };
            return;
        }

        var started = ep with
        {
            Cells = cells,
            SearchedDistance = searched,
            YieldRobotId = yieldRobot,
            YieldTo = yieldTo,
            YieldExpireTick = tick + _options.DeadlockResolveTtlTicks,
            LastAttemptTick = tick,
            AbsentSinceTick = null,
        };
        _episodes[key] = started;
        marked.Add(yieldRobot);
        directives.Add(Info(started, DeadlockPhase.Detected, "yield", 0, true));
    }

    /// 매 틱: 양보 완료(yield 발행·종료) · 양보 TTL 만료(재시도로 전환, 무발행) · 순환 소멸 유예 경과(reroute 발행·종료)를 본다(13 §5)
    public IReadOnlyList<DeadlockInfo> DetectCompletions(long tick, IReadOnlyDictionary<string, Point> positionOf)
    {
        var results = new List<DeadlockInfo>();
        foreach (var (key, ep) in _episodes.ToList())
        {
            if (ep.YieldRobotId is not null)
            {
                if (positionOf.TryGetValue(ep.YieldRobotId, out var pos) && pos == ep.YieldTo!.Value)
                {
                    results.Add(Info(ep, DeadlockPhase.Resolved, "yield", ElapsedMs(ep, tick), true));
                    _episodes.Remove(key);                                 // 양보 완료 → 에피소드 종료
                }
                else if (tick >= ep.YieldExpireTick)
                {
                    // TTL 만료: 양보가 제때 못 끝남 → 발행 없이 재시도로 전환, 에피소드 유지(다음에 다른 로봇을 양보시킬 수 있다)
                    _episodes[key] = ep with { YieldRobotId = null, YieldTo = null, LastAttemptTick = tick, AbsentSinceTick = null };
                }
            }
            else if (ep.AbsentSinceTick is { } since && tick - since >= _retryTicks)
            {
                results.Add(Info(ep, DeadlockPhase.Reroute, "reroute", ElapsedMs(ep, tick), true));
                _episodes.Remove(key);                                     // 순환 소멸(대안으로 빠짐) → 에피소드 종료
            }
        }

        return results;
    }

    /// run 종료: 남은 에피소드를 failed로 확정해 돌려준다(경과 resolvedMs, 마지막에 양보 로봇 없으면 yield_robot_id NULL). 멱등(13 §2 · §5)
    public IReadOnlyList<DeadlockInfo> DrainPending(long tick)
    {
        _drained ??= _episodes.Values.Select(e => Info(e, DeadlockPhase.Failed, "failed", ElapsedMs(e, tick), true)).ToList();
        return _drained;
    }

    // 순환의 각 로봇에 회피 지점을 BFS로 찾아 양보 비용 최소를 고른다. 아무도 못 찾으면 null. 13 §4
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

    private int ElapsedMs(Episode e, long tick) => (int)((tick - e.DetectTick) * _tickMs);

    private static DeadlockInfo Info(Episode e, DeadlockPhase phase, string resolution, int resolvedMs, bool isNew) =>
        new(e.Cycle, e.Cells, resolution, e.YieldRobotId, e.YieldTo, e.SearchedDistance, e.DetectTick, resolvedMs, phase, isNew,
            e.NoYieldRetries);

    // 막은 로봇 쪽으로의 단위 방향(인접하므로 한 축 ±1)
    private static Point Delta(Point from, Point blocker) => new(Math.Sign(blocker.X - from.X), Math.Sign(blocker.Y - from.Y));

    // dir이 가로면 같은 행, 세로면 같은 열이 축(회피 지점이 될 수 없는 직선)
    private static bool OnAxis(Point c, Point from, Point dir) => dir.X != 0 ? c.Y == from.Y : c.X == from.X;

    private static string Key(IReadOnlyList<string> cycle) =>
        string.Join('|', cycle.OrderBy(r => r, StringComparer.Ordinal));

    private sealed record Episode(
        IReadOnlyList<string> Cycle, IReadOnlyList<Point> Cells, long DetectTick, long LastAttemptTick,
        int NoYieldRetries, int SearchedDistance, string? YieldRobotId, Point? YieldTo, long YieldExpireTick, long? AbsentSinceTick);
}
