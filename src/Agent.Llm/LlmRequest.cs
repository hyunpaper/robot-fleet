namespace RobotFleet.Agent.Llm;

public enum LlmRequestKind
{
    Deadlock = 1,
    Infra = 2,
}

/// 조립 결과 = 호출 경계 입력. Messages API 필드 대응은 14 §5 대응표.
/// SystemPrompt · OutputSchemaJson은 프롬프트 버전마다 고정(캐시 접두), UserContent만 요청마다 바뀐다. 14 §3.7
public sealed record LlmRequest(
    LlmRequestKind Kind,
    string PromptVersion,
    string Model,
    LlmEffort Effort,
    int MaxTokens,
    TimeSpan Timeout,
    RefusalFallbackMode RefusalFallback,
    string SystemPrompt,
    string UserContent,
    string OutputSchemaJson);
