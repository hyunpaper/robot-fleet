using RobotFleet.Shared.Domain;

namespace RobotFleet.Simulator.Fleet;

/// 대기 작업 FIFO × 맨해튼 거리 최근접 Idle 로봇. 동점은 robotId 오름차순(결정적). 10 §1
public sealed class TaskDispatcher
{
    private readonly Queue<TransportTask> _pending = new();

    public int Pending => _pending.Count;

    public void Enqueue(TransportTask task) => _pending.Enqueue(task);

    /// 배정 쌍을 돌려준다. 로봇이 모자라면 남은 작업은 다음 틱으로
    public IReadOnlyList<(TransportTask Task, Robot Robot)> Dispatch(IEnumerable<Robot> idleRobots)
    {
        var idle = idleRobots.OrderBy(r => r.RobotId, StringComparer.Ordinal).ToList();
        var pairs = new List<(TransportTask, Robot)>();
        while (_pending.Count > 0 && idle.Count > 0)
        {
            var task = _pending.Dequeue();
            var best = idle[0];
            var bestDistance = Distance(best, task);
            foreach (var r in idle.Skip(1))
            {
                var d = Distance(r, task);
                if (d < bestDistance)
                {
                    best = r;
                    bestDistance = d;
                }
            }

            idle.Remove(best);
            pairs.Add((task, best));
        }

        return pairs;
    }

    private static int Distance(Robot r, TransportTask t) =>
        Math.Abs(r.Position.X - t.Origin.X) + Math.Abs(r.Position.Y - t.Origin.Y);
}
