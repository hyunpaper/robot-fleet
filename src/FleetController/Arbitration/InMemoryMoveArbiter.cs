using RobotFleet.Shared.Geometry;

namespace RobotFleet.FleetController.Arbitration;

/// <summary>
/// M2 메모리 셀 점유 판정. 로봇은 현재 칸을 점유하고, 승인된 이동 중에는 도착 칸까지 2칸을 점유한다(04 "2칸 선점").
/// 배치 판정 순서는 01 "배치 내 판정 순서" 1~3단계. M4에서 Redis 구현으로 교체된다.
/// </summary>
public sealed class InMemoryMoveArbiter : IMoveArbiter
{
    private readonly Dictionary<string, MapState> _maps = new();
    private readonly object _gate = new();

    /// <summary>로봇의 초기 위치를 점유로 등록한다. 미등록 로봇은 첫 요청의 From으로 자동 등록된다.</summary>
    public void Register(string mapId, string robotId, Point cell)
    {
        lock (_gate)
            MapOf(mapId).Register(robotId, cell);
    }

    /// <summary>칸을 점유한 로봇. 비어 있으면 null.</summary>
    public string? OccupantOf(string mapId, Point cell)
    {
        lock (_gate)
            return _maps.TryGetValue(mapId, out var map) && map.Owners.TryGetValue(cell, out var owner) ? owner : null;
    }

    public ValueTask<IReadOnlyList<MoveDecision>> DecideAsync(
        string mapId, long tick, IReadOnlyList<MoveRequest> requests, CancellationToken ct = default)
    {
        var decisions = new MoveDecision[requests.Count];
        var scores = new double[requests.Count];
        var order = new int[requests.Count];
        for (var i = 0; i < requests.Count; i++)
        {
            scores[i] = MovePriority.Score(requests[i]);
            order[i] = i;
        }
        Array.Sort(order, (a, b) =>
        {
            var c = scores[b].CompareTo(scores[a]);
            return c != 0 ? c : string.CompareOrdinal(requests[a].RobotId, requests[b].RobotId);
        });

        lock (_gate)
        {
            var map = MapOf(mapId);
            for (var k = 0; k < order.Length; k++)
            {
                var i = order[k];
                decisions[i] = Decide(map, requests[i], requests, order.AsSpan(0, k));
            }
        }
        return ValueTask.FromResult<IReadOnlyList<MoveDecision>>(decisions);
    }

    public ValueTask ReleaseAsync(string mapId, string robotId, Point cell, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_maps.TryGetValue(mapId, out var map))
                map.Release(robotId, cell);
        }
        return ValueTask.CompletedTask;
    }

    /// 2단계 구현: 진행 중 교착을 failed로 확정해 돌려준다(멱등). 지금은 교착 미연결이라 빈 목록. 13 §2 · §12
    public IReadOnlyList<DeadlockInfo> DrainPendingDeadlocks(string mapId, long tick) => Array.Empty<DeadlockInfo>();

    private static MoveDecision Decide(MapState map, MoveRequest r, IReadOnlyList<MoveRequest> all, ReadOnlySpan<int> ahead)
    {
        var robot = map.Robots.TryGetValue(r.RobotId, out var known) ? known : map.Register(r.RobotId, r.From);
        if (robot.Current != r.From)
            throw new InvalidOperationException($"{r.RobotId}: From {r.From} != 점유 중인 {robot.Current}");
        if (robot.Target is { } target)
        {
            if (target == r.To) return MoveDecision.Grant(r.RobotId);   // 같은 요청 재전송
            throw new InvalidOperationException($"{r.RobotId}: 이동 중({robot.Current} → {target}) 다른 칸 {r.To} 요청");
        }

        // 맞바꿈은 점유보다 먼저 본다. 선순위 쪽은 이미 occupied로 거부됐을 수 있다
        foreach (var j in ahead)
        {
            var other = all[j];
            if (other.To == r.From && other.From == r.To)
                return new MoveDecision(r.RobotId, false, MoveDenyReason.Swap, BlockedBy: other.RobotId);
        }

        if (map.Owners.TryGetValue(r.To, out var owner) && owner != r.RobotId)
            return new MoveDecision(r.RobotId, false, MoveDenyReason.Occupied, BlockedBy: owner);

        map.Owners[r.To] = r.RobotId;
        robot.Target = r.To;
        return MoveDecision.Grant(r.RobotId);
    }

    private MapState MapOf(string mapId)
    {
        if (!_maps.TryGetValue(mapId, out var map))
            _maps[mapId] = map = new MapState();
        return map;
    }

    private sealed class MapState
    {
        public readonly Dictionary<Point, string> Owners = new();
        public readonly Dictionary<string, RobotCells> Robots = new();

        public RobotCells Register(string robotId, Point cell)
        {
            if (Owners.TryGetValue(cell, out var owner) && owner != robotId)
                throw new InvalidOperationException($"{robotId}: {cell}은 {owner}가 점유 중");
            if (Robots.TryGetValue(robotId, out var old))
            {
                Owners.Remove(old.Current);
                if (old.Target is { } t) Owners.Remove(t);
            }
            Owners[cell] = robotId;
            return Robots[robotId] = new RobotCells { Current = cell };
        }

        public void Release(string robotId, Point cell)
        {
            if (!Robots.TryGetValue(robotId, out var robot)) return;
            if (robot.Current == cell && robot.Target is { } target)
            {
                robot.Current = target;   // 이동 완료: 도착 칸만 남는다
                robot.Target = null;
            }
            else if (robot.Current == cell || robot.Target == cell)
            {
                return;   // 지금 서 있거나 향하는 칸은 해제하지 않는다
            }
            if (Owners.TryGetValue(cell, out var owner) && owner == robotId)
                Owners.Remove(cell);
        }
    }

    private sealed class RobotCells
    {
        public Point Current;
        public Point? Target;
    }
}
