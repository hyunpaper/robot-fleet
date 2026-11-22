using System.Text.Json;
using RobotFleet.Agent.Llm;
using RobotFleet.Shared.Agent;
using RobotFleet.Shared.Geometry;
using RobotFleet.Shared.Tuning;

namespace RobotFleet.Agent.Llm.Tests;

/// 고정 입력. 스냅샷은 14 §2.5 예(설정 13개 = 04 기본값, set-1~set-13) — Shared.Tests AJ-01 골든과 같은 객체
internal static class TestFacts
{
    public const string Model = "claude-opus-5-5";

    public static DeadlockFactSnapshot NoYieldSnapshot(string snapshotId = "snap-20261122-01") => new(
        snapshotId,
        DeadlockCauseType.NoYieldPoint,
        ["run-a", "run-b"],
        [new CycleFact("cyc-1", ["R03", "R07"], [new Point(4, 2), new Point(5, 2)], 6, 4200, 3)],
        [new CellFact("cell-1", 4, 2, 180, 41, 0.228)],
        Settings(),
        [new MetricFact("met-1", "run-b", RunMetric.DeadlockRatePerHour, 3.5, "/h")],
        [new PastActionFact("act-1", "prop-12", TuningKey.DeadlockDetectWaitMs, 1000, 1500, "run-a", PastActionOutcome.AppliedNotImproved)]);

    public static IReadOnlyList<SettingFact> Settings() =>
        Enum.GetValues<TuningKey>()
            .Select(k => new SettingFact($"set-{(int)k}", k, TuningCatalog.Get(k).Default))
            .ToList();

    public static InfraIncidentFacts DlqIncident() => new(
        "inc-1",
        InfraIncidentKind.DlqDepth,
        new DateTimeOffset(2026, 11, 22, 1, 0, 0, TimeSpan.Zero),
        [
            new IncidentFact("f-1", InfraFactSource.Prometheus, "fleet_dlq_depth", 120, "건", null,
                new DateTimeOffset(2026, 11, 22, 1, 0, 0, TimeSpan.Zero)),
            new IncidentFact("f-2", InfraFactSource.Reconcile, "reconcile_result", null, null, "누락 0",
                new DateTimeOffset(2026, 11, 22, 1, 5, 0, TimeSpan.Zero)),
        ],
        [
            new InfraCandidate("cand-1", InfraActionId.I3, [new ActionArg("max_messages", "100")], "dlq-replay"),
            new InfraCandidate("cand-2", InfraActionId.B1, [], "pod-restart"),
        ]);

    public static LlmCompleted EndTurn(string? text, string model = Model) =>
        new("end_turn", text, null, model, new LlmUsage(1000, 200, 0, 0));

    public static LlmCompleted Stop(string? stopReason, string? text, string? refusalCategory = null) =>
        new(stopReason, text, refusalCategory, Model, new LlmUsage(1000, 200, 0, 0));

    /// 교착 응답 본문. 지정한 속성만 바꾼다(null 값 = JSON null). remove = 뺄 속성
    public static string DeadlockBody(
        string decision = "propose",
        string change = """{"parameter_key":"turn_cost","new_value":0.75}""",
        string coupled = "null",
        string cited = """["cell-1","set-2"]""",
        string rationale = "\"cell-1 혼잡도 0.228\"") =>
        $$"""{"rationale":{{rationale}},"cited_fact_ids":{{cited}},"decision":"{{decision}}","change":{{change}},"coupled_change":{{coupled}}}""";

    public static string Golden(string name) => ReadFile("Golden", name);

    public static string Fixture(string name) => ReadFile("Fixtures", name);

    private static string ReadFile(string folder, string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, folder, name)).Replace("\r\n", "\n").Trim();

    public static JsonElement ParseJson(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
