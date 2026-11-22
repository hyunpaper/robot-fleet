using FluentAssertions;
using RobotFleet.Shared.Agent;
using RobotFleet.Shared.Tuning;
using Xunit;

namespace RobotFleet.Agent.Llm.Tests;

/// 14 §4.1 · 4.2 교착 응답 파싱. LP-01~11
public class DeadlockParserTests
{
    private const string SnapshotId = "snap-20261122-01";

    private static LlmOutcome<DeadlockProposalDraft> Parse(LlmCallResult result) =>
        DeadlockResponseParser.Parse(result, SnapshotId);

    private static LlmOutcome<DeadlockProposalDraft>.NoProposal ShouldBeNone(LlmOutcome<DeadlockProposalDraft> outcome, NoProposalReason reason)
    {
        var none = outcome.Should().BeOfType<LlmOutcome<DeadlockProposalDraft>.NoProposal>().Subject;
        none.Reason.Should().Be(reason, none.Detail);
        return none;
    }

    [Fact] // LP-01
    public void Deadlock_EndTurn_SingleChange_Drafted()
    {
        var outcome = Parse(TestFacts.EndTurn(TestFacts.DeadlockBody(), model: "claude-opus-4-8"));

        var draft = outcome.Should().BeOfType<LlmOutcome<DeadlockProposalDraft>.Drafted>().Subject.Draft;
        draft.Change.Should().Be(new ParameterChange(TuningKey.TurnCost, 0.75));
        draft.CoupledChange.Should().BeNull();
        draft.Rationale.Should().Be("cell-1 혼잡도 0.228");
        draft.CitedFactIds.Should().Equal("cell-1", "set-2");
        draft.SnapshotId.Should().Be(SnapshotId);
        draft.PromptVersion.Should().Be("deadlock-v1");
        draft.AnsweredModel.Should().Be("claude-opus-4-8");   // 거절 대체 경로로 다른 모델이 답했을 수 있다(L8)
    }

    [Fact] // LP-02 — 14 §4.3 예(연동 제약 쌍)
    public void Deadlock_EndTurn_CoupledPair_Drafted()
    {
        var outcome = Parse(TestFacts.EndTurn(TestFacts.Fixture("response-propose-pair.json")));

        var draft = outcome.Should().BeOfType<LlmOutcome<DeadlockProposalDraft>.Drafted>().Subject.Draft;
        draft.Change.Should().Be(new ParameterChange(TuningKey.YieldSearchMaxDistance, 7));
        draft.CoupledChange.Should().Be(new ParameterChange(TuningKey.DeadlockResolveTtlTicks, 70));
        draft.CitedFactIds.Should().Equal("cyc-1", "set-10", "set-13");
        draft.Rationale.Should().StartWith("cyc-1 순환이 6회");
    }

    [Theory] // LP-03
    [InlineData("null")]
    [InlineData("""{"parameter_key":"turn_cost","new_value":0.75}""")]
    public void Deadlock_NoChange_NoProposal(string change)
    {
        var body = TestFacts.DeadlockBody(decision: "no_change", change: change, cited: "[]", rationale: "\"같은 키의 과거 조치가 개선 없이 끝났다\"");

        var none = ShouldBeNone(Parse(TestFacts.EndTurn(body)), NoProposalReason.NoChangeAdvised);

        none.Detail.Should().Be("같은 키의 과거 조치가 개선 없이 끝났다");
    }

    [Theory] // LP-04 — 거절이면 본문이 유효해도 읽지 않는다
    [InlineData("cyber", "cyber")]
    [InlineData(null, "")]
    public void Refusal_NoProposal_IgnoresBody(string? category, string expectedDetail)
    {
        var none = ShouldBeNone(Parse(TestFacts.Stop("refusal", TestFacts.DeadlockBody(), category)), NoProposalReason.Refusal);

        none.Detail.Should().Be(expectedDetail);
    }

    [Fact] // LP-05 — 잘린 본문은 InvalidJson이 아니라 MaxTokens
    public void MaxTokens_NoProposal()
    {
        var truncated = TestFacts.DeadlockBody()[..40];

        ShouldBeNone(Parse(TestFacts.Stop("max_tokens", truncated)), NoProposalReason.MaxTokens);
    }

    [Theory] // LP-06
    [InlineData("tool_use")]
    [InlineData("pause_turn")]
    [InlineData("stop_sequence")]
    [InlineData("new_reason")]
    [InlineData(null)]
    public void OtherStopReasons_Unexpected(string? stopReason)
    {
        var none = ShouldBeNone(Parse(TestFacts.Stop(stopReason, TestFacts.DeadlockBody())), NoProposalReason.UnexpectedStopReason);

        none.Detail.Should().Be(stopReason ?? "null");
    }

    [Theory] // LP-07
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \n")]
    public void EmptyText_EmptyResponse(string? text)
    {
        ShouldBeNone(Parse(TestFacts.EndTurn(text)), NoProposalReason.EmptyResponse);
    }

    public static TheoryData<string> MalformedBodies => new()
    {
        TestFacts.DeadlockBody()[..^1],                               // 잘린 JSON
        "```json\n" + TestFacts.DeadlockBody() + "\n```",              // 코드 펜스
        TestFacts.DeadlockBody() + "\n이상입니다.",                      // JSON 뒤 문장
        TestFacts.DeadlockBody().Replace("}", ",}", StringComparison.Ordinal),   // 뒤 쉼표
    };

    [Theory] // LP-08
    [MemberData(nameof(MalformedBodies))]
    public void MalformedJson_InvalidJson(string body)
    {
        ShouldBeNone(Parse(TestFacts.EndTurn(body)), NoProposalReason.InvalidJson);
    }

    [Theory] // LP-08 — JSON으로는 유효하지만 최상위가 객체가 아님
    [InlineData("[]", "$")]
    [InlineData("\"propose\"", "$")]
    public void NonObjectRoot_SchemaViolation(string body, string path)
    {
        var none = ShouldBeNone(Parse(TestFacts.EndTurn(body)), NoProposalReason.SchemaViolation);

        none.Detail.Should().StartWith(path + ":");
    }

    public static TheoryData<string, string> SchemaViolations => new()
    {
        { """{"rationale":"r","cited_fact_ids":["a"],"decision":"propose","change":null}""", "$.coupled_change" },
        { TestFacts.DeadlockBody().Replace("\"decision\"", "\"extra\":1,\"decision\"", StringComparison.Ordinal), "$.extra" },
        { TestFacts.DeadlockBody(change: """{"parameter_key":"tick_ms","new_value":100}"""), "$.change.parameter_key" },
        { TestFacts.DeadlockBody(change: """{"parameter_key":"turn_cost","new_value":0.75,"unit":"x"}"""), "$.change.unit" },
        { TestFacts.DeadlockBody(change: """{"parameter_key":"yield_search_max_distance","new_value":7}""",
            coupled: """{"parameter_key":"turn_cost","new_value":0.5}"""), "$.coupled_change.parameter_key" },
        { TestFacts.DeadlockBody(change: "null"), "$.change" },
        { TestFacts.DeadlockBody(change: """{"parameter_key":"yield_search_max_distance","new_value":7.5}"""), "$.change.new_value" },
        { TestFacts.DeadlockBody(change: """{"parameter_key":"yield_search_max_distance","new_value":7}""",
            coupled: """{"parameter_key":"deadlock_resolve_ttl_ticks","new_value":70.5}"""), "$.coupled_change.new_value" },
        { TestFacts.DeadlockBody(cited: "[]"), "$.cited_fact_ids" },
        { TestFacts.DeadlockBody(cited: """["cell-1",""]"""), "$.cited_fact_ids[1]" },
        { TestFacts.DeadlockBody(cited: """["cell-1",3]"""), "$.cited_fact_ids[1]" },
        { TestFacts.DeadlockBody(change: """{"parameter_key":"turn_cost","new_value":"0.75"}"""), "$.change.new_value" },
        { TestFacts.DeadlockBody(decision: "maybe"), "$.decision" },
        { TestFacts.DeadlockBody(rationale: "null"), "$.rationale" },
        { TestFacts.DeadlockBody().Replace("\"decision\":\"propose\"", "\"decision\":\"propose\",\"decision\":\"propose\"", StringComparison.Ordinal), "$.decision" },
    };

    [Theory] // LP-09
    [MemberData(nameof(SchemaViolations))]
    public void Deadlock_SchemaViolations(string body, string path)
    {
        var none = ShouldBeNone(Parse(TestFacts.EndTurn(body)), NoProposalReason.SchemaViolation);

        none.Detail.Should().StartWith(path + ":");
    }

    public static TheoryData<string, string> OutOfRangeBodies => new()
    {
        { TestFacts.DeadlockBody(change: """{"parameter_key":"turn_cost","new_value":9.0}"""), "범위 밖" },
        { TestFacts.DeadlockBody(change: """{"parameter_key":"replan_cooldown_ms","new_value":900}"""), "1회 상한 초과" },
        { TestFacts.DeadlockBody(change: """{"parameter_key":"yield_search_max_distance","new_value":9}"""), "연동 제약 위반(TTL 동반 없음)" },
        { TestFacts.DeadlockBody(change: """{"parameter_key":"turn_cost","new_value":0.75}""",
            coupled: """{"parameter_key":"deadlock_resolve_ttl_ticks","new_value":10}"""), "쌍이 아닌 2개 변경" },
    };

    [Theory] // LP-10 — 값의 타당성은 ControlApi 검증 0~4(L6). 파서는 통과시킨다
    [MemberData(nameof(OutOfRangeBodies))]
    public void Deadlock_OutOfRangeValue_StillDrafted(string body, string why)
    {
        Parse(TestFacts.EndTurn(body)).Should().BeOfType<LlmOutcome<DeadlockProposalDraft>.Drafted>(why);
    }

    [Theory] // LP-11
    [InlineData(LlmFailureKind.ApiError, NoProposalReason.ApiError)]
    [InlineData(LlmFailureKind.Timeout, NoProposalReason.Timeout)]
    public void Failed_MapsToReason(LlmFailureKind kind, NoProposalReason expected)
    {
        var none = ShouldBeNone(Parse(new LlmFailed(kind, "529 overloaded_error")), expected);

        none.Detail.Should().Be("529 overloaded_error");
    }
}
