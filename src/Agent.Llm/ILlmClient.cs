namespace RobotFleet.Agent.Llm;

/// 호출 경계. 제안 1건 = 요청 1회(12). 14 §5
public interface ILlmClient
{
    /// 실패(API 오류 · 시간 초과)는 예외가 아니라 LlmFailed로 돌려준다. ct 취소(호출자 종료)만 OperationCanceledException
    Task<LlmCallResult> SendAsync(LlmRequest request, CancellationToken ct);
}
