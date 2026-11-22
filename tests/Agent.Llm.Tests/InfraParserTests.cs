using FluentAssertions;
using RobotFleet.Shared.Agent;
using Xunit;

namespace RobotFleet.Agent.Llm.Tests;

/// 14 §4.3 인프라 응답 파싱. LP-12~14 (+ 공통 분기 확인)
public class InfraParserTests
{
    private static LlmOutcome<InfraExplanationDraft> Parse(LlmCallResult result) =>
        InfraResponseParser.Parse(result, TestFacts.DlqIncident());

    private static LlmOutcome<InfraExplanationDraft>.NoProposal ShouldBeNone(LlmOutcome<InfraExplanationDraft> outcome, NoProposalReason reason)
    {
        var none = outcome.Should().BeOfType<LlmOutcome<InfraExplanationDraft>.NoProposal>().Subject;
        none.Reason.Should().Be(reason, none.Detail);
        return none;
    }

    [Fact] // LP-12 — 후보 2개 중 1개만 설명해도 통과
    public void Infra_EndTurn_Drafted()
    {
        const string body = """
            {"explanations":[{"candidate_id":"cand-1","explanation":"DLQ 적체 120건(f-1)을 원 큐로 다시 보낸다. 중복은 워커 영수증이 막는다.","cited_fact_ids":["f-1"]}]}
            """;

        var outcome = Parse(TestFacts.EndTurn(body));

        var draft = outcome.Should().BeOfType<LlmOutcome<InfraExplanationDraft>.Drafted>().Subject.Draft;
        draft.IncidentId.Should().Be("inc-1");
        draft.PromptVersion.Should().Be("infra-v1");
        draft.AnsweredModel.Should().Be(TestFacts.Model);
        draft.Explanations.Should().ContainSingle();
        draft.Explanations[0].CandidateId.Should().Be("cand-1");
        draft.Explanations[0].Explanation.Should().StartWith("DLQ 적체 120건");
        draft.Explanations[0].CitedFactIds.Should().Equal("f-1");
    }

    [Fact] // LP-12 — 순서는 응답 그대로
    public void Infra_KeepsResponseOrder()
    {
        const string body = """
            {"explanations":[{"candidate_id":"cand-2","explanation":"b","cited_fact_ids":[]},{"candidate_id":"cand-1","explanation":"a","cited_fact_ids":["f-1"]}]}
            """;

        var draft = Parse(TestFacts.EndTurn(body)).Should().BeOfType<LlmOutcome<InfraExplanationDraft>.Drafted>().Subject.Draft;

        draft.Explanations.Select(e => e.CandidateId).Should().Equal("cand-2", "cand-1");
    }

    [Theory] // LP-13 — 응답 전체를 버린다
    [InlineData("""{"explanations":[{"candidate_id":"cand-1","explanation":"a","cited_fact_ids":[]},{"candidate_id":"cand-9","explanation":"b","cited_fact_ids":[]}]}""", "$.explanations[1].candidate_id")]
    [InlineData("""{"explanations":[{"candidate_id":"cand-1","explanation":"a","cited_fact_ids":[]},{"candidate_id":"cand-1","explanation":"b","cited_fact_ids":[]}]}""", "$.explanations[1].candidate_id")]
    [InlineData("""{"explanations":[{"candidate_id":"cand-1","explanation":"a"}]}""", "$.explanations[0].cited_fact_ids")]
    [InlineData("""{"explanations":{"candidate_id":"cand-1"}}""", "$.explanations")]
    [InlineData("""{"explanations":[],"summary":"x"}""", "$.summary")]
    public void Infra_UnknownOrDuplicateCandidate_SchemaViolation(string body, string path)
    {
        var none = ShouldBeNone(Parse(TestFacts.EndTurn(body)), NoProposalReason.SchemaViolation);

        none.Detail.Should().StartWith(path + ":");
    }

    [Fact] // LP-14
    public void Infra_EmptyExplanations_EmptyResponse()
    {
        ShouldBeNone(Parse(TestFacts.EndTurn("""{"explanations":[]}""")), NoProposalReason.EmptyResponse);
    }

    [Fact] // §4.1 분기는 교착과 같다
    public void Infra_StopReasonsAndFailures()
    {
        ShouldBeNone(Parse(TestFacts.Stop("refusal", """{"explanations":[]}""", "cyber")), NoProposalReason.Refusal);
        ShouldBeNone(Parse(TestFacts.Stop("max_tokens", """{"explanations":[""")), NoProposalReason.MaxTokens);
        ShouldBeNone(Parse(TestFacts.EndTurn("{\"explanations\":")), NoProposalReason.InvalidJson);
        ShouldBeNone(Parse(new LlmFailed(LlmFailureKind.Timeout, "120s")), NoProposalReason.Timeout);
    }
}
