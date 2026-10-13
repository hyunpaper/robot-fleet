namespace RobotFleet.Shared.Pipeline;

/// 저장 오류를 결정적(재시도 무의미)과 일시적(재시도 가능)으로 가른다. 10 §6
/// 결정적 = SQLSTATE 22(데이터 오류) · 23(제약 위반) · 42(문법 · 접근 규칙). 배치를 몇 번 다시 보내도 같은 결과다.
/// 일시적 = 08(연결) · 53(자원 부족) · 57(관리자 개입 · 취소) · 그 밖(타임아웃 · 소켓). 재시도하면 풀린다.
/// 지금은 결정적 오류 하나로 파티션이 영구 정지(같은 배치 무한 재시도)하므로, 결정적 오류만 따로 잡아 DLQ로 보낸다
public static class DbErrorClass
{
    /// SQLSTATE 앞 두 자리로 판정. null · 빈 값은 일시적으로 본다(연결 단계 오류는 SQLSTATE가 없다)
    public static bool IsDeterministicSqlState(string? sqlState)
    {
        if (string.IsNullOrEmpty(sqlState) || sqlState.Length < 2) return false;
        var cls = sqlState[..2];
        return cls is "22" or "23" or "42";
    }
}

/// 저장소가 결정적 오류를 이 타입으로 감싸 올리면, 파티션 처리기는 재시도 대신 원인 메시지를 DLQ로 보낸다.
/// 일시 오류는 감싸지 않고 그대로 올려 재시도에 맡긴다. 저장소 구현(Npgsql 등)에만 PostgresException 의존을 둔다
public sealed class DeterministicStoreException : Exception
{
    public DeterministicStoreException(string message, string? sqlState, Exception inner) : base(message, inner) =>
        SqlState = sqlState;

    public string? SqlState { get; }
}
