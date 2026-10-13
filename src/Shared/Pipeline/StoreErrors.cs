namespace RobotFleet.Shared.Pipeline;

/// 저장 오류 갈래. 10 §6
public enum StoreErrorKind
{
    /// 연결 · 직렬화 실패 · 교착 · 자원 · 관리자 개입 · 타임아웃. 재시도하면 풀린다 — ack · nack 없이 재시도
    Transient,

    /// 데이터 · 제약 위반(SQLSTATE 22 · 23). 특정 메시지가 원인일 수 있다 — 분할 격리로 그 메시지만 DLQ(배치당 상한 1건)
    Data,

    /// 문법 · 접근(42) · 미지원(0A) · 미분류 코드. 배포 · 마이그레이션 어긋남이라 배치 내용과 무관하게 전부 실패 —
    /// 분할해도 큐 전체를 DLQ로 비울 뿐이다. 정지 + 경보로 두고 DB가 고쳐지면 자동 재개
    System,
}

/// 저장 오류가 오면 처리기가 할 행동. 10 §6
public enum StoreAction
{
    /// 같은 배치를 상한 백오프로 재시도(일시 오류)
    Retry,

    /// 분할 없이 같은 배치를 재시도하며 파티션 정지 유지 + 경보(시스템 오류 · 데이터 DLQ 상한 초과)
    Halt,

    /// 배치를 절반씩 나눠 원인을 좁힌다(데이터 오류, 배치 2건 이상)
    Split,

    /// 1건까지 좁혀진 데이터 오류 메시지를 DLQ로
    IsolateToDlq,
}

/// 저장 오류 분류 · 행동 결정. 둘 다 순수 함수라 인프라 없이 테스트한다. 10 §6
public static class DbErrorClass
{
    /// SQLSTATE 앞 두 자리로 갈래를 정한다. null · 빈 값은 일시적(연결 단계 오류는 SQLSTATE가 없다).
    /// 22 · 23 = 데이터, 42 · 0A = 시스템, 08 · 40 · 53 · 57 = 일시, 그 밖(미분류) = 시스템
    public static StoreErrorKind Classify(string? sqlState)
    {
        if (string.IsNullOrEmpty(sqlState) || sqlState.Length < 2) return StoreErrorKind.Transient;
        return sqlState[..2] switch
        {
            "08" or "40" or "53" or "57" => StoreErrorKind.Transient,
            "22" or "23" => StoreErrorKind.Data,
            _ => StoreErrorKind.System,   // 42 · 0A · 그 밖 미분류 코드
        };
    }

    /// 갈래 · 남은 (하위)배치 크기 · 남은 DLQ 예산으로 행동을 정한다.
    /// 데이터 오류는 1건까지 좁히되 배치당 DLQ 상한(예산)을 넘으면 정지로 전환한다 —
    /// 매핑 버그로 특정 이벤트 타입 전부가 23502를 내는 경우를 시스템 오류로 방어
    public static StoreAction Decide(StoreErrorKind kind, int batchSize, int dlqBudget) => kind switch
    {
        StoreErrorKind.Transient => StoreAction.Retry,
        StoreErrorKind.System => StoreAction.Halt,
        _ => batchSize <= 1 ? (dlqBudget > 0 ? StoreAction.IsolateToDlq : StoreAction.Halt) : StoreAction.Split,
    };
}

/// 저장소가 일시적이 아닌 오류를 이 타입으로 감싸 올리면, 처리기가 갈래(`Kind`)에 따라 격리 · 정지를 고른다.
/// 일시 오류는 감싸지 않고 그대로 올려 재시도에 맡긴다. PostgresException 의존은 저장소 구현에만 둔다
public sealed class ClassifiedStoreException : Exception
{
    public ClassifiedStoreException(string message, StoreErrorKind kind, string? sqlState, Exception inner)
        : base(message, inner)
    {
        Kind = kind;
        SqlState = sqlState;
    }

    public StoreErrorKind Kind { get; }
    public string? SqlState { get; }
}
