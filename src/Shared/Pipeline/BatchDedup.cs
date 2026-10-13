namespace RobotFleet.Shared.Pipeline;

/// 한 파티션 배치 안의 eventId(= Delivery.MessageId) 중복 제거. 10 §6
/// Ingest 2단계 멱등의 "발행 후 확정 실패 → 재송신" 구간에서 같은 eventId가 한 배치에 두 번 들어올 수 있다.
/// 그대로 두면 PgEventStore가 event_id로 ToDictionary · UPSERT를 하면서 터지고(파티션 정지),
/// 상태 이벤트는 두 번째 사본이 robot_state 기준과 어긋나 invalid로 잘못 통지된다. 적재 직전에 처음 1건만 남긴다
public static class BatchDedup
{
    /// 처음 나온 eventId만 원래 순서로 남기고, 뒤의 중복은 센다.
    /// MessageId가 빈 delivery는 중복 판정에서 빼고 그대로 둔다(역직렬화 단계에서 독 메시지로 걸러진다 — 빈 id끼리 묶지 않는다)
    public static DedupResult ByEventId(IReadOnlyList<Delivery> batch)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var unique = new List<Delivery>(batch.Count);
        var duplicates = 0;
        foreach (var d in batch)
        {
            if (!string.IsNullOrEmpty(d.MessageId) && !seen.Add(d.MessageId))
            {
                duplicates++;
                continue;
            }

            unique.Add(d);
        }

        return new DedupResult(unique, duplicates);
    }
}

/// Unique = 적재할 delivery(원래 순서), Duplicates = 걸러낸 중복 수(메트릭 · 중복 집계)
public readonly record struct DedupResult(IReadOnlyList<Delivery> Unique, int Duplicates);
