using System.Text.Json.Nodes;
using FluentAssertions;
using RobotFleet.Agent.Llm.Prompts;
using RobotFleet.Agent.Llm.Schemas;
using RobotFleet.Shared.Agent;
using RobotFleet.Shared.Tuning;
using Xunit;

namespace RobotFleet.Agent.Llm.Tests;

/// 14 §3 요청 조립. LR-02~09
public class RequestBuilderTests
{
    private static readonly LlmRequestBuilder s_builder = new(new LlmOptions());

    [Fact] // LR-02
    public void SystemPrompts_RenderedWithoutPlaceholders()
    {
        var deadlock = PromptTemplates.DeadlockSystem;
        var infra = PromptTemplates.InfraSystem;

        deadlock.Should().NotContain("{{").And.NotContain("\r");
        infra.Should().NotContain("{{").And.NotContain("\r");
        foreach (var p in TuningCatalog.All)
            deadlock.Should().Contain($"| {p.Name} |");
        deadlock.Should().Contain(CatalogTableRenderer.Render());
        deadlock.Should().StartWith("너는 창고 로봇 관제 시스템의 교착 분석 보조자다.");
        foreach (var id in Enum.GetNames<InfraActionId>())
            infra.Should().Contain($"| {id} |");
    }

    [Fact] // LR-03
    public void DeadlockSchema_Golden()
    {
        var actual = JsonNode.Parse(OutputSchemas.Deadlock)!;
        var golden = JsonNode.Parse(TestFacts.Golden("deadlock-schema.json"))!;

        JsonNode.DeepEquals(actual, golden).Should().BeTrue(actual.ToJsonString());
        PropertyOrder(actual).Should().Equal(PropertyOrder(golden));   // 근거 → 결정 순서(14 §3.5)

        var change = actual["properties"]!["change"]!["anyOf"]![1]!;
        Strings(change["properties"]!["parameter_key"]!["enum"]!)
            .Should().Equal(TuningCatalog.All.Select(p => p.Name));
        var coupled = actual["properties"]!["coupled_change"]!["anyOf"]![1]!;
        Strings(coupled["properties"]!["parameter_key"]!["enum"]!)
            .Should().Equal(TuningCatalog.Couplings.Select(c => TuningCatalog.Get(c.Dependent).Name));
    }

    [Fact] // LR-03
    public void InfraSchema_Golden()
    {
        var actual = JsonNode.Parse(OutputSchemas.Infra)!;
        var golden = JsonNode.Parse(TestFacts.Golden("infra-schema.json"))!;

        JsonNode.DeepEquals(actual, golden).Should().BeTrue(actual.ToJsonString());
    }

    [Theory] // LR-04
    [InlineData("deadlock")]
    [InlineData("infra")]
    public void Schemas_AllObjectsClosedAndFullyRequired(string which)
    {
        var schema = JsonNode.Parse(which == "deadlock" ? OutputSchemas.Deadlock : OutputSchemas.Infra)!;
        var objects = 0;

        Walk(schema, node =>
        {
            node.ContainsKey("minimum").Should().BeFalse();
            node.ContainsKey("maximum").Should().BeFalse();
            node.ContainsKey("minItems").Should().BeFalse();
            node.ContainsKey("maxItems").Should().BeFalse();
            if ((string?)node["type"] != "object")
                return;

            objects++;
            node["additionalProperties"]!.GetValue<bool>().Should().BeFalse();
            var properties = node["properties"]!.AsObject().Select(p => p.Key);
            Strings(node["required"]!).Should().Equal(properties);
        });

        objects.Should().BeGreaterThan(0);
    }

    [Fact] // LR-05 — 사용자 메시지 = 태그 + AgentJsonContext 직렬화(Shared.Tests AJ-01 골든)
    public void ForDeadlock_UserContent_WrapsSnapshotJson()
    {
        var request = s_builder.ForDeadlock(TestFacts.NoYieldSnapshot());

        request.UserContent.Should().Be("<fact_snapshot>\n" + TestFacts.Golden("snapshot-no-yield.json") + "\n</fact_snapshot>");
    }

    [Fact] // LR-05 (인프라)
    public void ForInfra_UserContent_WrapsIncidentJson()
    {
        var request = s_builder.ForInfra(TestFacts.DlqIncident());

        request.UserContent.Should().StartWith("<incident>\n{\"incident_id\":\"inc-1\"").And.EndWith("}\n</incident>");
    }

    [Fact] // LR-06
    public void ForDeadlock_SystemPromptIndependentOfSnapshot()
    {
        var a = s_builder.ForDeadlock(TestFacts.NoYieldSnapshot("snap-a"));
        var other = TestFacts.NoYieldSnapshot("snap-b") with
        {
            CauseType = DeadlockCauseType.Starvation,
            Settings = TestFacts.Settings().Select(s => s with { CurrentValue = TuningCatalog.Get(s.Key).Max }).ToList(),
        };
        var b = s_builder.ForDeadlock(other);

        b.SystemPrompt.Should().Be(a.SystemPrompt);
        b.OutputSchemaJson.Should().Be(a.OutputSchemaJson);
        b.UserContent.Should().NotBe(a.UserContent);
        a.SystemPrompt.Should().NotContain("snap-a");
    }

    [Fact] // LR-07
    public void ForDeadlock_RequestFieldsFromOptions()
    {
        var deadlock = s_builder.ForDeadlock(TestFacts.NoYieldSnapshot());
        var infra = s_builder.ForInfra(TestFacts.DlqIncident());

        deadlock.Kind.Should().Be(LlmRequestKind.Deadlock);
        deadlock.PromptVersion.Should().Be("deadlock-v1");
        deadlock.Model.Should().Be("claude-opus-5-5");
        deadlock.Effort.Should().Be(LlmEffort.High);
        deadlock.MaxTokens.Should().Be(16000);
        deadlock.Timeout.Should().Be(TimeSpan.FromSeconds(300));
        deadlock.RefusalFallback.Should().Be(RefusalFallbackMode.Default);
        deadlock.SystemPrompt.Should().Be(PromptTemplates.DeadlockSystem);
        deadlock.OutputSchemaJson.Should().Be(OutputSchemas.Deadlock);

        infra.Kind.Should().Be(LlmRequestKind.Infra);
        infra.PromptVersion.Should().Be("infra-v1");
        infra.Effort.Should().Be(LlmEffort.Medium);
        infra.MaxTokens.Should().Be(16000);
        infra.Timeout.Should().Be(TimeSpan.FromSeconds(120));
        infra.SystemPrompt.Should().Be(PromptTemplates.InfraSystem);
        infra.OutputSchemaJson.Should().Be(OutputSchemas.Infra);

        var custom = new LlmRequestBuilder(new LlmOptions { Model = "claude-sonnet-5-5", RefusalFallback = RefusalFallbackMode.Off })
            .ForDeadlock(TestFacts.NoYieldSnapshot());
        custom.Model.Should().Be("claude-sonnet-5-5");
        custom.RefusalFallback.Should().Be(RefusalFallbackMode.Off);
    }

    public static TheoryData<string> InvalidSnapshots => new()
    {
        "settings 12개", "settings 키 중복", "사실 id 중복", "빈 사실 id", "빈 run_ids", "빈 snapshot_id",
    };

    [Theory] // LR-08
    [MemberData(nameof(InvalidSnapshots))]
    public void ForDeadlock_InvalidSnapshot_Throws(string which)
    {
        var s = TestFacts.NoYieldSnapshot();
        var snapshot = which switch
        {
            "settings 12개" => s with { Settings = s.Settings.Skip(1).ToList() },
            "settings 키 중복" => s with { Settings = s.Settings.Take(12).Append(s.Settings[0] with { Id = "set-x" }).ToList() },
            "사실 id 중복" => s with { Cells = [s.Cells[0] with { Id = "cyc-1" }] },
            "빈 사실 id" => s with { Metrics = [s.Metrics[0] with { Id = " " }] },
            "빈 run_ids" => s with { RunIds = [] },
            _ => s with { SnapshotId = "" },
        };

        var act = () => s_builder.ForDeadlock(snapshot);

        act.Should().Throw<ArgumentException>();
    }

    public static TheoryData<string> InvalidIncidents => new() { "후보 0개", "후보 id 중복", "사실 id 중복", "빈 incident_id" };

    [Theory] // LR-09
    [MemberData(nameof(InvalidIncidents))]
    public void ForInfra_InvalidIncident_Throws(string which)
    {
        var i = TestFacts.DlqIncident();
        var incident = which switch
        {
            "후보 0개" => i with { Candidates = [] },
            "후보 id 중복" => i with { Candidates = [i.Candidates[0], i.Candidates[1] with { CandidateId = "cand-1" }] },
            "사실 id 중복" => i with { Facts = [i.Facts[0], i.Facts[1] with { Id = "f-1" }] },
            _ => i with { IncidentId = " " },
        };

        var act = () => s_builder.ForInfra(incident);

        act.Should().Throw<ArgumentException>();
    }

    private static IEnumerable<string> PropertyOrder(JsonNode schema) =>
        schema["properties"]!.AsObject().Select(p => p.Key).ToList();

    private static List<string> Strings(JsonNode array) =>
        array.AsArray().Select(n => n!.GetValue<string>()).ToList();

    private static void Walk(JsonNode? node, Action<JsonObject> visit)
    {
        switch (node)
        {
            case JsonObject obj:
                visit(obj);
                foreach (var (_, child) in obj)
                    Walk(child, visit);
                break;
            case JsonArray array:
                foreach (var child in array)
                    Walk(child, visit);
                break;
        }
    }
}
