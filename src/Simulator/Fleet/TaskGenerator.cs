using RobotFleet.Shared.Domain;
using RobotFleet.Shared.Geometry;
using RobotFleet.Shared.Map;

namespace RobotFleet.Simulator.Fleet;

/// 포아송 작업 생성(03 "샘플 데이터 생성 규칙"). 틱당 기대값 λ × tickMs / 1000, 출발 · 목적은 Station 무작위(같으면 재추첨)
public sealed class TaskGenerator
{
    private readonly GridMap _map;
    private readonly double _perTick;
    private readonly Random _rng;
    private int _seq;

    public TaskGenerator(GridMap map, double lambdaPerSecond, int tickMs, int seed)
    {
        if (map.Stations.Count < 2)
            throw new ArgumentException($"작업 생성에는 Station이 2개 이상 필요하다 ({map.MapId}: {map.Stations.Count}개)", nameof(map));
        _map = map;
        _perTick = lambdaPerSecond * tickMs / 1000.0;
        _rng = new Random(seed);
    }

    public int Created => _seq;

    public IReadOnlyList<TransportTask> Next(DateTimeOffset now)
    {
        var count = Poisson();
        var tasks = new List<TransportTask>(count);
        for (var i = 0; i < count; i++)
        {
            var origin = _map.Stations[_rng.Next(_map.Stations.Count)];
            var destination = origin;
            while (destination.Id == origin.Id)
                destination = _map.Stations[_rng.Next(_map.Stations.Count)];
            tasks.Add(new TransportTask
            {
                TaskId = $"T-{++_seq:D5}",
                MapId = _map.MapId,
                Type = TaskType.Transport,
                Origin = new Point(origin.X, origin.Y),
                Destination = new Point(destination.X, destination.Y),
                CreatedAt = now,
            });
        }

        return tasks;
    }

    // Knuth. 틱당 기대값이 작아(보통 < 1) 충분히 빠르다
    private int Poisson()
    {
        if (_perTick <= 0) return 0;
        var limit = Math.Exp(-_perTick);
        var k = 0;
        var p = 1.0;
        do
        {
            k++;
            p *= _rng.NextDouble();
        }
        while (p > limit);
        return k - 1;
    }
}
