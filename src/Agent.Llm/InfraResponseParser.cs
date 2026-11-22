using System.Text.Json;
using RobotFleet.Agent.Llm.Prompts;
using RobotFleet.Shared.Agent;

namespace RobotFleet.Agent.Llm;

/// 인프라 응답 → 후보별 설명 또는 제안 없음. 후보 id는 입력 후보와 대조한다. 예외를 던지지 않는다(인자 null 제외). 14 §4.3
public static class InfraResponseParser
{
    private const string Explanations = "explanations";
    private const string CandidateId = "candidate_id";
    private const string Explanation = "explanation";
    private const string CitedFactIds = "cited_fact_ids";

    public static LlmOutcome<InfraExplanationDraft> Parse(LlmCallResult result, InfraIncidentFacts incident)
    {
        ArgumentNullException.ThrowIfNull(incident);

        if (StopReasonGate.Check(result, out var completed) is { } stop)
            return None(stop.Reason, stop.Detail);
        if (ResponseBody.TryOpen(completed!.Text, out var document) is { } open)
            return None(open.Reason, open.Detail);

        using (document)
        {
            try
            {
                return ParseBody(document!.RootElement, incident, completed.AnsweredModel);
            }
            catch (SchemaViolationException ex)
            {
                return None(NoProposalReason.SchemaViolation, ex.Message);
            }
        }
    }

    private static LlmOutcome<InfraExplanationDraft> ParseBody(JsonElement root, InfraIncidentFacts incident, string answeredModel)
    {
        ResponseBody.RequireObject(root, "$", Explanations);
        var array = root.GetProperty(Explanations);
        if (array.ValueKind != JsonValueKind.Array)
            throw new SchemaViolationException("$.explanations", $"배열이어야 한다({array.ValueKind})");

        var known = incident.Candidates.Select(c => c.CandidateId).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var explanations = new List<CandidateExplanation>();
        var i = 0;
        foreach (var item in array.EnumerateArray())
        {
            var path = $"$.explanations[{i}]";
            ResponseBody.RequireObject(item, path, CandidateId, Explanation, CitedFactIds);
            var candidateId = ResponseBody.String(item, CandidateId, path);
            var text = ResponseBody.String(item, Explanation, path);
            var cited = ResponseBody.StringArray(item, CitedFactIds, path);

            // 지어낸 후보 id가 있으면 같은 응답의 다른 설명도 믿기 어렵다 — 전체를 버린다(규칙 후보는 그대로 뜬다)
            if (!known.Contains(candidateId))
                throw new SchemaViolationException($"{path}.{CandidateId}", $"입력에 없는 후보({candidateId})");
            if (!seen.Add(candidateId))
                throw new SchemaViolationException($"{path}.{CandidateId}", $"중복 후보({candidateId})");

            explanations.Add(new CandidateExplanation(candidateId, text, cited));
            i++;
        }

        if (explanations.Count == 0)
            return None(NoProposalReason.EmptyResponse, "설명 0건");

        return new LlmOutcome<InfraExplanationDraft>.Drafted(new InfraExplanationDraft(
            incident.IncidentId, PromptTemplates.InfraVersion, answeredModel, explanations));
    }

    private static LlmOutcome<InfraExplanationDraft> None(NoProposalReason reason, string detail) =>
        new LlmOutcome<InfraExplanationDraft>.NoProposal(reason, detail);
}
