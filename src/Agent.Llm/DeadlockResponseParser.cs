using System.Text.Json;
using RobotFleet.Agent.Llm.Prompts;
using RobotFleet.Shared.Agent;
using RobotFleet.Shared.Tuning;

namespace RobotFleet.Agent.Llm;

/// 교착 응답 → 제안 초안 또는 제안 없음. 형식만 본다 — 범위 · 1회 상한 · 연동 제약 · 쌍의 합법성 · 인용 id 실재는
/// ControlApi 검증 0~4(L6). 예외를 던지지 않는다(인자 null 제외). 14 §4.2
public static class DeadlockResponseParser
{
    private const string Rationale = "rationale";
    private const string CitedFactIds = "cited_fact_ids";
    private const string Decision = "decision";
    private const string Change = "change";
    private const string CoupledChange = "coupled_change";
    private const string ParameterKey = "parameter_key";
    private const string NewValue = "new_value";

    public static LlmOutcome<DeadlockProposalDraft> Parse(LlmCallResult result, string snapshotId)
    {
        ArgumentNullException.ThrowIfNull(snapshotId);

        if (StopReasonGate.Check(result, out var completed) is { } stop)
            return None(stop.Reason, stop.Detail);
        if (ResponseBody.TryOpen(completed!.Text, out var document) is { } open)
            return None(open.Reason, open.Detail);

        using (document)
        {
            try
            {
                return ParseBody(document!.RootElement, snapshotId, completed.AnsweredModel);
            }
            catch (SchemaViolationException ex)
            {
                return None(NoProposalReason.SchemaViolation, ex.Message);
            }
        }
    }

    private static LlmOutcome<DeadlockProposalDraft> ParseBody(JsonElement root, string snapshotId, string answeredModel)
    {
        // 3 · 4단계: 구조와 키. 스키마가 보장하지만 다시 본다 — 구조화 출력이 빠진 요청에도 같은 결과
        ResponseBody.RequireObject(root, "$", Rationale, CitedFactIds, Decision, Change, CoupledChange);
        var rationale = ResponseBody.String(root, Rationale, "$");
        var cited = ResponseBody.StringArray(root, CitedFactIds, "$");
        var decision = ResponseBody.String(root, Decision, "$");
        if (decision is not ("propose" or "no_change"))
            throw new SchemaViolationException("$.decision", $"propose 또는 no_change여야 한다({decision})");

        var change = ReadChange(root.GetProperty(Change), "$.change", dependentOnly: false);
        var coupled = ReadChange(root.GetProperty(CoupledChange), "$.coupled_change", dependentOnly: true);

        // 5단계
        if (decision == "no_change")
            return None(NoProposalReason.NoChangeAdvised, rationale);

        // 6 · 7 · 8단계: 스키마가 표현하지 못하는 형식 규칙(조건부 필수 · 키별 형식 · 최소 개수)
        if (change is null)
            throw new SchemaViolationException("$.change", "propose인데 null");
        RequireIntegerIfNeeded(change, "$.change.new_value");
        if (coupled is not null)
            RequireIntegerIfNeeded(coupled, "$.coupled_change.new_value");
        if (cited.Count == 0)
            throw new SchemaViolationException("$.cited_fact_ids", "propose에는 인용이 1개 이상 필요하다");
        for (var i = 0; i < cited.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(cited[i]))
                throw new SchemaViolationException($"$.cited_fact_ids[{i}]", "빈 id");
        }

        return new LlmOutcome<DeadlockProposalDraft>.Drafted(new DeadlockProposalDraft(
            snapshotId, PromptTemplates.DeadlockVersion, answeredModel, change, coupled, rationale, cited));
    }

    private static ParameterChange? ReadChange(JsonElement element, string path, bool dependentOnly)
    {
        if (element.ValueKind == JsonValueKind.Null)
            return null;

        ResponseBody.RequireObject(element, path, ParameterKey, NewValue);
        var name = ResponseBody.String(element, ParameterKey, path);
        var value = ResponseBody.Number(element, NewValue, path);

        if (!TuningCatalog.TryParseName(name, out var key))
            throw new SchemaViolationException($"{path}.{ParameterKey}", $"등급 A 카탈로그 밖의 키({name})");
        if (dependentOnly && !TuningCatalog.Couplings.Any(c => c.Dependent == key))
            throw new SchemaViolationException($"{path}.{ParameterKey}", $"연동 제약의 종속 키가 아니다({name})");

        return new ParameterChange(key, value);
    }

    private static void RequireIntegerIfNeeded(ParameterChange change, string path)
    {
        if (TuningCatalog.Get(change.Key).Kind == TuningValueKind.Integer && change.NewValue != Math.Floor(change.NewValue))
            throw new SchemaViolationException(path, FormattableString.Invariant($"정수 키에 정수가 아닌 값({change.NewValue})"));
    }

    private static LlmOutcome<DeadlockProposalDraft> None(NoProposalReason reason, string detail) =>
        new LlmOutcome<DeadlockProposalDraft>.NoProposal(reason, detail);
}
