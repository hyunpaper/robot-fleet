namespace RobotFleet.Shared.Tuning;

/// 등급 A 카탈로그 — 키 · 기본값 · 허용 범위 · 1회 상한 · 연동 제약의 단일 출처. 값은 04 표 그대로. 14 §2.1
public static class TuningCatalog
{
    private static readonly TuningParameter[] Parameters =
    [
        new(TuningKey.CongestionAlpha, "congestion_alpha", TuningValueKind.Decimal,
            TuningDefaults.CongestionAlpha, 0, 2.0, 0.3, "경로 탐색 — 혼잡 가중치", "경로 효율, 혼잡 손실률"),
        new(TuningKey.TurnCost, "turn_cost", TuningValueKind.Decimal,
            TuningDefaults.TurnCost, 0, 2.0, 0.25, "경로 탐색 — 회전 비용", "경로 효율, 리드타임"),
        new(TuningKey.PriorityWaitWeight, "priority.wait_weight", TuningValueKind.Decimal,
            TuningDefaults.PriorityWaitWeight, 0, 0.1, 0.01, "우선순위 정책", "리드타임 p99, 작업 실패율"),
        new(TuningKey.PriorityDistanceWeight, "priority.distance_weight", TuningValueKind.Decimal,
            TuningDefaults.PriorityDistanceWeight, 0, 3.0, 0.5, "우선순위 정책", "평균 리드타임"),
        new(TuningKey.PriorityTaskPriorityWeight, "priority.task_priority_weight", TuningValueKind.Decimal,
            TuningDefaults.PriorityTaskPriorityWeight, 0, 200, 25, "우선순위 정책", "긴급 작업 리드타임"),
        new(TuningKey.ReplanWaitThresholdMs, "replan_wait_threshold_ms", TuningValueKind.Integer,
            TuningDefaults.ReplanWaitThresholdMs, 200, 2000, 250, "재계획 트리거", "재계획률, 혼잡 손실률"),
        new(TuningKey.ReplanCooldownMs, "replan_cooldown_ms", TuningValueKind.Integer,
            TuningDefaults.ReplanCooldownMs, 50, 1000, 100, "재계획 폭주 방지", "재계획률, 판단 p99"),
        new(TuningKey.ReplanMaxPerTask, "replan_max_per_task", TuningValueKind.Integer,
            TuningDefaults.ReplanMaxPerTask, 3, 10, 2, "재계획 폭주 방지", "작업 실패율"),
        new(TuningKey.DeadlockDetectWaitMs, "deadlock_detect_wait_ms", TuningValueKind.Integer,
            TuningDefaults.DeadlockDetectWaitMs, 500, 3000, 500, "교착 탐지", "교착 발생률, 해소 시간"),
        new(TuningKey.YieldSearchMaxDistance, "yield_search_max_distance", TuningValueKind.Integer,
            TuningDefaults.YieldSearchMaxDistance, 3, 15, 3, "해소 — 양보", "교착 해소 시간, 작업 실패율"),
        new(TuningKey.YieldCostDistanceWeight, "yield_cost.distance_weight", TuningValueKind.Decimal,
            TuningDefaults.YieldCostDistanceWeight, 0, 3.0, 0.5, "해소 — 양보 비용", "교착 해소 시간"),
        new(TuningKey.YieldCostPriorityWeight, "yield_cost.priority_weight", TuningValueKind.Decimal,
            TuningDefaults.YieldCostPriorityWeight, 0, 3.0, 0.5, "해소 — 양보 비용", "긴급 작업 영향"),
        new(TuningKey.DeadlockResolveTtlTicks, "deadlock_resolve_ttl_ticks", TuningValueKind.Integer,
            TuningDefaults.DeadlockResolveTtlTicks, 30, 120, 20, "교착 해소 락", "재탐지 빈도"),
    ];

    private static readonly TuningCoupling[] CouplingList =
    [
        new(TuningKey.DeadlockResolveTtlTicks, TuningKey.YieldSearchMaxDistance, 10),
    ];

    /// 04 번호 순 13개
    public static IReadOnlyList<TuningParameter> All => Parameters;

    /// 지금 1개: deadlock_resolve_ttl_ticks >= yield_search_max_distance × 10
    public static IReadOnlyList<TuningCoupling> Couplings => CouplingList;

    public static TuningParameter Get(TuningKey key)
    {
        var i = (int)key - 1;
        if (i < 0 || i >= Parameters.Length || Parameters[i].Key != key)
            throw new ArgumentOutOfRangeException(nameof(key), key, "알 수 없는 튜닝 키");
        return Parameters[i];
    }

    /// Name(04 키 원문, 대소문자 구분) → TuningKey. 모르는 이름이면 false
    public static bool TryParseName(string name, out TuningKey key)
    {
        foreach (var p in Parameters)
        {
            if (string.Equals(p.Name, name, StringComparison.Ordinal))
            {
                key = p.Key;
                return true;
            }
        }

        key = default;
        return false;
    }

    /// Min ≤ value ≤ Max, Integer 키는 정수. 어기면
    /// ArgumentException("{Name}({value})는 {Min}~{Max} 범위여야 한다(04 허용 범위)")
    public static void ValidateRange(TuningKey key, double value)
    {
        var p = Get(key);
        if (!(value >= p.Min && value <= p.Max))
            throw new ArgumentException(FormattableString.Invariant($"{p.Name}({value})는 {p.Min}~{p.Max} 범위여야 한다(04 허용 범위)"));
        if (p.Kind == TuningValueKind.Integer && value != Math.Floor(value))
            throw new ArgumentException(FormattableString.Invariant($"{p.Name}({value})는 정수여야 한다"));
    }

    /// 모든 연동 제약 검사. 어기면 ArgumentException(메시지에 두 키 Name 포함)
    public static void ValidateCouplings(Func<TuningKey, double> valueOf)
    {
        foreach (var c in CouplingList)
        {
            var dep = valueOf(c.Dependent);
            var drv = valueOf(c.Driver);
            if (dep < drv * c.Factor)
            {
                throw new ArgumentException(FormattableString.Invariant(
                    $"{Get(c.Dependent).Name}({dep})는 {Get(c.Driver).Name}({drv}) × {c.Factor} 이상이어야 한다"));
            }
        }
    }
}
