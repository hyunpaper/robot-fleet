using FluentAssertions;
using Xunit;

namespace RobotFleet.Agent.Llm.Tests;

/// 실제 연결 구현체를 그대로 부른다(대체 구현이 아니다). M9에서 본문을 채우면 이 테스트를 지우고
/// 실제 호출은 [IntegrationFact](키 없으면 skip)로 따로 둔다. 14 §5 · §6
public class AnthropicLlmClientTests
{
    [Fact] // LC-01
    public async Task AnthropicLlmClient_NotImplemented()
    {
        var options = new LlmOptions();
        var client = new AnthropicLlmClient(options);
        var request = new LlmRequestBuilder(options).ForDeadlock(TestFacts.NoYieldSnapshot());

        var act = () => client.SendAsync(request, CancellationToken.None);

        (await act.Should().ThrowAsync<NotImplementedException>())
            .WithMessage("*docs/14-m9-llm-client.md*");
    }
}
