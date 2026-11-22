namespace RobotFleet.Shared.Tuning;

/// 등급 A 카탈로그 — 키 · 기본값 · 허용 범위 · 1회 상한 · 연동 제약의 단일 출처. 값은 04 표 그대로. 14 §2.1
public static class TuningCatalog
{
    /// 04 번호 순 13개
    public static IReadOnlyList<TuningParameter> All => throw new NotImplementedException();

    /// 지금 1개: deadlock_resolve_ttl_ticks >= yield_search_max_distance × 10
    public static IReadOnlyList<TuningCoupling> Couplings => throw new NotImplementedException();

    public static TuningParameter Get(TuningKey key) => throw new NotImplementedException();

    /// Name(04 키 원문, 대소문자 구분) → TuningKey. 모르는 이름이면 false
    public static bool TryParseName(string name, out TuningKey key) => throw new NotImplementedException();

    /// Min ≤ value ≤ Max, Integer 키는 정수. 어기면
    /// ArgumentException("{Name}({value})는 {Min}~{Max} 범위여야 한다(04 허용 범위)")
    public static void ValidateRange(TuningKey key, double value) => throw new NotImplementedException();

    /// 모든 연동 제약 검사. 어기면 ArgumentException(메시지에 두 키 Name 포함)
    public static void ValidateCouplings(Func<TuningKey, double> valueOf) => throw new NotImplementedException();
}
