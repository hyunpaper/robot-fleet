using RobotFleet.Shared.Geometry;

namespace RobotFleet.TraceExport;

public static class TraceChecks
{
    /// <summary>같은 틱에 두 로봇이 같은 칸을 점유한 경우를 모두 찾는다. 이동 중이면 출발 칸과 도착 칸을 모두 점유한 것으로 본다.</summary>
    public static IReadOnlyList<string> FindConflicts(Trace trace)
    {
        var conflicts = new List<string>();
        foreach (var frame in trace.Ticks)
        {
            var owner = new Dictionary<Point, string>();
            for (var i = 0; i < frame.Robots.Count; i++)
            {
                var r = frame.Robots[i];
                var id = trace.Robots[i].Id;
                foreach (var cell in Occupied(r))
                {
                    if (owner.TryGetValue(cell, out var other) && other != id)
                        conflicts.Add($"tick {frame.Tick}: {other} and {id} both occupy {cell}");
                    else
                        owner[cell] = id;
                }
            }
        }
        return conflicts;
    }

    private static IEnumerable<Point> Occupied(RobotFrame r)
    {
        yield return new Point(r.X, r.Y);
        if (r is { Act: "Move", Tx: { } tx, Ty: { } ty })
            yield return new Point(tx, ty);
    }
}
