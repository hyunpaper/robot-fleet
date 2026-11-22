namespace RobotFleet.Agent.Llm;

/// 설정 섹션 `Agent:Llm`. API 키는 두지 않는다(환경 변수 ANTHROPIC_API_KEY, SDK가 읽는다). 14 §3.6
public sealed record LlmOptions
{
    public const string SectionName = "Agent:Llm";

    public string Model { get; init; } = "claude-opus-5-5";

    /// Default = fallbacks: "default" + 베타 헤더 server-side-fallback-2026-07-01 (L8, 총괄 확정)
    public RefusalFallbackMode RefusalFallback { get; init; } = RefusalFallbackMode.Default;

    public LlmCallOptions Deadlock { get; init; } = new() { Effort = LlmEffort.High, MaxTokens = 16000, TimeoutSeconds = 300 };

    public LlmCallOptions Infra { get; init; } = new() { Effort = LlmEffort.Medium, MaxTokens = 16000, TimeoutSeconds = 120 };

    /// Model 비어 있지 않음, MaxTokens 1 ~ 64000, TimeoutSeconds 1 ~ 600 (Deadlock · Infra 각각). 어기면 ArgumentException
    public void Validate() => throw new NotImplementedException();
}

/// 요청 종류별 설정. TimeoutSeconds = SDK 재시도까지 합친 호출 전체 예산
public sealed record LlmCallOptions
{
    public LlmEffort Effort { get; init; } = LlmEffort.High;

    public int MaxTokens { get; init; } = 16000;

    public int TimeoutSeconds { get; init; } = 300;
}

/// 전송 값 low · medium · high · xhigh · max (output_config.effort)
public enum LlmEffort
{
    Low = 1,
    Medium = 2,
    High = 3,
    XHigh = 4,
    Max = 5,
}

public enum RefusalFallbackMode
{
    Off = 0,
    Default = 1,
}
