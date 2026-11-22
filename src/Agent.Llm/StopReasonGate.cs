namespace RobotFleet.Agent.Llm;

/// 호출 결과 → 본문 파싱 여부. end_turn만 본문으로 간다. 14 §4.1
internal static class StopReasonGate
{
    /// null = 본문 파싱으로 진행(completed는 end_turn 응답). 아니면 제안 없음 사유
    public static (NoProposalReason Reason, string Detail)? Check(LlmCallResult result, out LlmCompleted? completed)
    {
        ArgumentNullException.ThrowIfNull(result);
        completed = null;

        switch (result)
        {
            case LlmFailed { Kind: LlmFailureKind.Timeout } f:
                return (NoProposalReason.Timeout, f.Detail);
            case LlmFailed f:
                return (NoProposalReason.ApiError, f.Detail);
            case LlmCompleted c:
                switch (c.StopReason)
                {
                    case "end_turn":
                        completed = c;
                        return null;
                    case "refusal":       // 본문이 있어도 읽지 않는다 — 거절 응답은 스키마를 따르지 않을 수 있다
                        return (NoProposalReason.Refusal, c.RefusalCategory ?? "");
                    case "max_tokens":    // 잘린 JSON은 읽지 않는다
                        return (NoProposalReason.MaxTokens, "max_tokens");
                    default:
                        return (NoProposalReason.UnexpectedStopReason, c.StopReason ?? "null");
                }
            default:
                return (NoProposalReason.ApiError, "알 수 없는 호출 결과 " + result.GetType().Name);
        }
    }
}
