namespace RobotFleet.Agent.Llm;

/// 연결 구현체가 SDK 응답 · 예외를 바꿔 돌려주는 값. 파서는 이것만 받는다. 14 §4.1
public abstract record LlmCallResult;

/// Text = text 블록을 순서대로 이어 붙인 것(thinking 블록 제외). RefusalCategory = stop_details.category(refusal일 때만)
public sealed record LlmCompleted(
    string? StopReason,
    string? Text,
    string? RefusalCategory,
    string AnsweredModel,
    LlmUsage Usage) : LlmCallResult;

public sealed record LlmFailed(LlmFailureKind Kind, string Detail) : LlmCallResult;

public enum LlmFailureKind
{
    ApiError = 1,
    Timeout = 2,
}

public sealed record LlmUsage(long InputTokens, long OutputTokens, long CacheReadInputTokens, long CacheCreationInputTokens);
