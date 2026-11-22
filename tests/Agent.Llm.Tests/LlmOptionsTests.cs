using FluentAssertions;
using Xunit;

namespace RobotFleet.Agent.Llm.Tests;

public class LlmOptionsTests
{
    // LR-10
    [Fact]
    public void LlmOptions_Validate()
    {
        var o = new LlmOptions();
        o.Validate();

        o.Model.Should().Be("claude-opus-5-5");
        o.RefusalFallback.Should().Be(RefusalFallbackMode.Default);
        o.Deadlock.Should().Be(new LlmCallOptions { Effort = LlmEffort.High, MaxTokens = 16000, TimeoutSeconds = 300 });
        o.Infra.Should().Be(new LlmCallOptions { Effort = LlmEffort.Medium, MaxTokens = 16000, TimeoutSeconds = 120 });

        var bad = new List<LlmOptions>
        {
            o with { Model = "" },
            o with { Model = "  " },
            o with { RefusalFallback = (RefusalFallbackMode)99 },
        };
        foreach (var call in new[] { true, false })
        {
            LlmOptions With(Func<LlmCallOptions, LlmCallOptions> f) =>
                call ? o with { Deadlock = f(o.Deadlock) } : o with { Infra = f(o.Infra) };

            bad.Add(With(c => c with { MaxTokens = 0 }));
            bad.Add(With(c => c with { MaxTokens = 64001 }));
            bad.Add(With(c => c with { TimeoutSeconds = 0 }));
            bad.Add(With(c => c with { TimeoutSeconds = 601 }));
            bad.Add(With(c => c with { Effort = (LlmEffort)99 }));
        }

        foreach (var b in bad)
        {
            var act = () => b.Validate();
            act.Should().Throw<ArgumentException>();
        }

        var tokens = () => (o with { Deadlock = o.Deadlock with { MaxTokens = 0 } }).Validate();
        tokens.Should().Throw<ArgumentException>().WithMessage("*Agent:Llm:Deadlock:MaxTokens*");
        var timeout = () => (o with { Infra = o.Infra with { TimeoutSeconds = 601 } }).Validate();
        timeout.Should().Throw<ArgumentException>().WithMessage("*Agent:Llm:Infra:TimeoutSeconds*");

        (o with { Deadlock = o.Deadlock with { MaxTokens = 64000, TimeoutSeconds = 600 } }).Validate();
        (o with { Deadlock = o.Deadlock with { MaxTokens = 1, TimeoutSeconds = 1 } }).Validate();
    }
}
