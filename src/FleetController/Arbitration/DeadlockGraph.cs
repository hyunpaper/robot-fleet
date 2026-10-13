namespace RobotFleet.FleetController.Arbitration;

/// 대기 그래프 순환 탐지(함수형, out-degree ≤ 1 → 서로소 순환). 순수 함수. 13 §3
public static class DeadlockGraph
{
    /// waitEdges: robotId → blockedBy(이번 틱 거부만). waitMs: robotId → 누적 대기ms.
    /// 반환: 서로소 순환들(각 순환은 thresholdMs 이상 대기 로봇을 하나 이상 포함). 각 순환은 최소 robotId에서 시작하도록
    /// 회전하고, 순환 목록은 그 첫 원소로 정렬 — 시작 순서·요청 순서와 무관하게 결정적(13 §9)
    public static IReadOnlyList<IReadOnlyList<string>> FindCycles(
        IReadOnlyDictionary<string, string> waitEdges, IReadOnlyDictionary<string, int> waitMs, int thresholdMs)
    {
        var cycles = new List<IReadOnlyList<string>>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        foreach (var start in waitEdges.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (visited.Contains(start)) continue;

            var path = new List<string>();
            var pos = new Dictionary<string, int>(StringComparer.Ordinal);
            string? cur = start;
            while (cur is not null && !visited.Contains(cur))
            {
                if (pos.TryGetValue(cur, out var idx))
                {
                    var cycle = path.GetRange(idx, path.Count - idx);   // ρ 모양의 고리 부분만
                    if (cycle.Any(r => waitMs.GetValueOrDefault(r) >= thresholdMs))
                        cycles.Add(Canonical(cycle));
                    break;
                }

                pos[cur] = path.Count;
                path.Add(cur);
                cur = waitEdges.TryGetValue(cur, out var next) ? next : null;
            }

            foreach (var r in path) visited.Add(r);   // 걸어 본 노드는 다시 걷지 않는다
        }

        return cycles.OrderBy(c => c[0], StringComparer.Ordinal).ToList();
    }

    // 최소 robotId가 맨 앞에 오도록 고리를 회전(순환 순서는 유지). cycleKey·귀속 로봇이 결정적
    private static List<string> Canonical(List<string> cycle)
    {
        var min = 0;
        for (var i = 1; i < cycle.Count; i++)
            if (string.CompareOrdinal(cycle[i], cycle[min]) < 0) min = i;

        var result = new List<string>(cycle.Count);
        for (var i = 0; i < cycle.Count; i++)
            result.Add(cycle[(min + i) % cycle.Count]);
        return result;
    }
}
