namespace RobotFleet.Agent.Llm;

/// 파싱 결과. 파서는 예외 없이 둘 중 하나로 끝난다. 14 §4.1
public abstract record LlmOutcome<TDraft>
    where TDraft : class
{
    public sealed record Drafted(TDraft Draft) : LlmOutcome<TDraft>;

    public sealed record NoProposal(NoProposalReason Reason, string Detail) : LlmOutcome<TDraft>;
}

public enum NoProposalReason
{
    NoChangeAdvised = 1,
    Refusal = 2,
    MaxTokens = 3,
    UnexpectedStopReason = 4,
    EmptyResponse = 5,
    InvalidJson = 6,
    SchemaViolation = 7,
    ApiError = 8,
    Timeout = 9,
}
