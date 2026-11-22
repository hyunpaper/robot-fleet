namespace RobotFleet.Agent.Llm;

/// Claude Messages API 연결 구현체. 지금은 비워 둔다 — 키를 읽거나 네트워크를 열지 않는다(L1).
/// M9에서 SDK(Anthropic NuGet)를 참조해 14 §5 대응표대로 채운다. 미구현 예외를 제안 없음으로 바꾸지 않는다
public sealed class AnthropicLlmClient : ILlmClient
{
    public AnthropicLlmClient(LlmOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        Options = options;
    }

    public LlmOptions Options { get; }

    public Task<LlmCallResult> SendAsync(LlmRequest request, CancellationToken ct) =>
        throw new NotImplementedException(
            "Claude API 연결은 M9에서 구현한다(docs/14-m9-llm-client.md §5). 지금은 요청 조립 · 응답 파싱만 있다.");
}
