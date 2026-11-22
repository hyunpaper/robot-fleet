using System.Text.Json;
using RobotFleet.Agent.Llm.Prompts;
using RobotFleet.Agent.Llm.Schemas;
using RobotFleet.Shared.Agent;
using RobotFleet.Shared.Tuning;

namespace RobotFleet.Agent.Llm;

/// 사실 스냅샷 · 사건 → LlmRequest. 시스템 프롬프트 · 스키마는 고정(캐시 접두), 사용자 메시지만 요청마다 바뀐다. 14 §3.7
/// 입력 결함은 ArgumentException — 스냅샷은 ControlApi가 만들므로 어긋나면 호출 쪽 결함이다(제안 없음으로 삼키지 않는다)
public sealed class LlmRequestBuilder
{
    private readonly LlmOptions _options;

    public LlmRequestBuilder(LlmOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
    }

    public LlmRequest ForDeadlock(DeadlockFactSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateDeadlock(snapshot);

        var json = JsonSerializer.Serialize(snapshot, AgentJsonContext.Default.DeadlockFactSnapshot);
        return Build(LlmRequestKind.Deadlock, PromptTemplates.DeadlockVersion, _options.Deadlock,
            PromptTemplates.DeadlockSystem, Wrap("fact_snapshot", json), OutputSchemas.Deadlock);
    }

    public LlmRequest ForInfra(InfraIncidentFacts incident)
    {
        ArgumentNullException.ThrowIfNull(incident);
        ValidateInfra(incident);

        var json = JsonSerializer.Serialize(incident, AgentJsonContext.Default.InfraIncidentFacts);
        return Build(LlmRequestKind.Infra, PromptTemplates.InfraVersion, _options.Infra,
            PromptTemplates.InfraSystem, Wrap("incident", json), OutputSchemas.Infra);
    }

    private LlmRequest Build(LlmRequestKind kind, string version, LlmCallOptions call,
        string systemPrompt, string userContent, string schema) =>
        new(kind, version, _options.Model, call.Effort, call.MaxTokens, TimeSpan.FromSeconds(call.TimeoutSeconds),
            _options.RefusalFallback, systemPrompt, userContent, schema);

    private static string Wrap(string tag, string json) => $"<{tag}>\n{json}\n</{tag}>";

    private static void ValidateDeadlock(DeadlockFactSnapshot s)
    {
        RequireText(s.SnapshotId, "snapshot_id");
        if (s.RunIds is null || s.RunIds.Count == 0 || s.RunIds.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("run_ids는 비어 있지 않은 run id 1개 이상이어야 한다(complete run만, 12)");

        // 13개 키가 정확히 한 번씩 — 하나라도 빠지면 LLM이 1회 상한을 계산할 수 없다(14 §2.2)
        var settings = Require(s.Settings, "settings");
        var keys = settings.Select(f => f.Key).ToList();
        var expected = Enum.GetValues<TuningKey>();
        if (keys.Count != expected.Length || keys.Distinct().Count() != keys.Count || !expected.All(keys.Contains))
            throw new ArgumentException($"settings는 등급 A {expected.Length}개 키를 정확히 한 번씩 담아야 한다({keys.Count}개)");

        RequireUniqueIds(
            Require(s.Cycles, "cycles").Select(f => f.Id)
                .Concat(Require(s.Cells, "cells").Select(f => f.Id))
                .Concat(settings.Select(f => f.Id))
                .Concat(Require(s.Metrics, "metrics").Select(f => f.Id))
                .Concat(Require(s.PastActions, "past_actions").Select(f => f.Id)),
            "사실 id");
    }

    private static void ValidateInfra(InfraIncidentFacts incident)
    {
        RequireText(incident.IncidentId, "incident_id");
        var candidates = Require(incident.Candidates, "candidates");
        if (candidates.Count == 0)
            throw new ArgumentException("candidates가 없다 — 설명할 후보가 없으면 호출하지 않는다");

        RequireUniqueIds(candidates.Select(c => c.CandidateId), "후보 id");
        RequireUniqueIds(Require(incident.Facts, "facts").Select(f => f.Id), "사실 id");
    }

    private static void RequireUniqueIds(IEnumerable<string> ids, string what)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            RequireText(id, what);
            if (!seen.Add(id))
                throw new ArgumentException($"{what} 중복: {id}");
        }
    }

    private static void RequireText(string? value, string what)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{what}가 비어 있다");
    }

    private static IReadOnlyList<T> Require<T>(IReadOnlyList<T>? list, string what) =>
        list ?? throw new ArgumentException($"{what}가 null이다");
}
