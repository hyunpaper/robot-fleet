using RobotFleet.Shared.Geometry;

namespace RobotFleet.TraceExport;

public static class TraceChecks
{
    /// <summary>
    /// frames 안에서 같은 틱에 두 로봇이 같은 칸을 점유한 경우를 모두 찾는다.
    /// 이동 중이면 출발 칸과 도착 칸을 모두 점유한 것으로 본다.
    /// </summary>
    public static IReadOnlyList<string> FindConflicts(IReadOnlyList<RobotInfo> robots, IEnumerable<TickFrame> frames)
    {
        var conflicts = new List<string>();
        foreach (var frame in frames)
        {
            var owner = new Dictionary<Point, string>();
            for (var i = 0; i < frame.Robots.Count; i++)
            {
                var id = robots[i].Id;
                foreach (var cell in Occupied(frame.Robots[i]))
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

    /// <summary>모든 로봇 상태가 period 틱마다 반복되는지. 처음 어긋난 틱을 돌려주고, 없으면 null.</summary>
    public static long? FindPeriodBreak(IReadOnlyList<TickFrame> frames, int from, int period)
    {
        for (var i = from; i + period < frames.Count; i++)
        {
            if (!frames[i].Robots.SequenceEqual(frames[i + period].Robots))
                return frames[i].Tick;
        }
        return null;
    }

    /// <summary>대기 구간 목록. "R-003 @ (6,13) 12 ticks, blocked cell (6,12)" 꼴.</summary>
    public static IReadOnlyList<string> WaitEpisodes(IReadOnlyList<RobotInfo> robots, IReadOnlyList<TickFrame> frames)
    {
        var episodes = new List<string>();
        for (var i = 0; i < robots.Count; i++)
        {
            var start = -1;
            for (var t = 0; t <= frames.Count; t++)
            {
                var waiting = t < frames.Count && frames[t].Robots[i].Status == "Waiting";
                if (waiting && start < 0) start = t;
                if (waiting || start < 0) continue;
                var r = frames[start].Robots[i];
                episodes.Add($"{robots[i].Id} @ ({r.X},{r.Y}) from tick {frames[start].Tick}, {t - start} ticks, blocked cell ({r.Wx},{r.Wy})");
                start = -1;
            }
        }
        return episodes;
    }

    private static IEnumerable<Point> Occupied(RobotFrame r)
    {
        yield return new Point(r.X, r.Y);
        if (r is { Act: "Move", Tx: { } tx, Ty: { } ty })
            yield return new Point(tx, ty);
    }
}
