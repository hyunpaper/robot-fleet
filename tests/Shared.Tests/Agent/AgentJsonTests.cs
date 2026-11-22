using System.Text.Json;
using FluentAssertions;
using RobotFleet.Shared.Agent;
using RobotFleet.Shared.Geometry;
using RobotFleet.Shared.Tuning;
using Xunit;

namespace RobotFleet.Shared.Tests.Agent;

public class AgentJsonTests
{
    private static string Golden(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Agent", "Golden", name)).Replace("\r\n", "\n").Trim();

    private static DeadlockFactSnapshot Snapshot()
    {
        double[] defaults =
        [
            TuningDefaults.CongestionAlpha, TuningDefaults.TurnCost, TuningDefaults.PriorityWaitWeight,
            TuningDefaults.PriorityDistanceWeight, TuningDefaults.PriorityTaskPriorityWeight,
            TuningDefaults.ReplanWaitThresholdMs, TuningDefaults.ReplanCooldownMs, TuningDefaults.ReplanMaxPerTask,
            TuningDefaults.DeadlockDetectWaitMs, TuningDefaults.YieldSearchMaxDistance,
            TuningDefaults.YieldCostDistanceWeight, TuningDefaults.YieldCostPriorityWeight,
            TuningDefaults.DeadlockResolveTtlTicks,
        ];
        var settings = Enum.GetValues<TuningKey>()
            .OrderBy(k => (int)k)
            .Select(k => new SettingFact($"set-{(int)k}", k, defaults[(int)k - 1]))
            .ToList();
        return new DeadlockFactSnapshot(
            "snap-20261122-01", DeadlockCauseType.NoYieldPoint, ["run-a", "run-b"],
            [new CycleFact("cyc-1", ["R03", "R07"], [new Point(4, 2), new Point(5, 2)], 6, 4200, 3)],
            [new CellFact("cell-1", 4, 2, 180, 41, 0.228)],
            settings,
            [new MetricFact("met-1", "run-b", RunMetric.DeadlockRatePerHour, 3.5, "/h")],
            [new PastActionFact("act-1", "prop-12", TuningKey.DeadlockDetectWaitMs, 1000, 1500, "run-a",
                PastActionOutcome.AppliedNotImproved)]);
    }

    [Fact]
    public void DeadlockSnapshot_Json_GoldenAndRoundTrip()
    {
        var json = JsonSerializer.Serialize(Snapshot(), AgentJsonContext.Default.DeadlockFactSnapshot);

        json.Should().Be(Golden("snapshot-no-yield.json"));
        json.Should().Contain("\"key\":\"priority.wait_weight\"")
            .And.Contain("\"cause_type\":\"no_yield_point\"")
            .And.Contain("\"outcome\":\"applied_not_improved\"")
            .And.Contain("\"metric\":\"deadlock_rate_per_hour\"");

        var back = JsonSerializer.Deserialize(json, AgentJsonContext.Default.DeadlockFactSnapshot)!;
        back.Settings.Should().HaveCount(13);
        JsonSerializer.Serialize(back, AgentJsonContext.Default.DeadlockFactSnapshot).Should().Be(json);
    }

    [Fact]
    public void InfraIncident_Json_GoldenAndRoundTrip()
    {
        var incident = new InfraIncidentFacts(
            "inc-20261122-01", InfraIncidentKind.DlqDepth, new DateTimeOffset(2026, 11, 22, 3, 15, 0, TimeSpan.Zero),
            [
                new IncidentFact("f-1", InfraFactSource.Prometheus, "dlq_depth", 42, "msgs", null,
                    new DateTimeOffset(2026, 11, 22, 3, 14, 50, TimeSpan.Zero)),
                new IncidentFact("f-2", InfraFactSource.Kubernetes, "worker_pod", null, null, "event-worker-0",
                    new DateTimeOffset(2026, 11, 22, 3, 14, 55, TimeSpan.Zero)),
            ],
            [
                new InfraCandidate("c-1", InfraActionId.I3, [new ActionArg("partition", "p2")], "R-DLQ-1"),
                new InfraCandidate("c-2", InfraActionId.B1, [], "R-DLQ-2"),
            ]);

        var json = JsonSerializer.Serialize(incident, AgentJsonContext.Default.InfraIncidentFacts);

        json.Should().Be(Golden("incident-dlq.json"));
        json.Should().Contain("\"action\":\"I3\"").And.NotContain("null");

        var back = JsonSerializer.Deserialize(json, AgentJsonContext.Default.InfraIncidentFacts)!;
        JsonSerializer.Serialize(back, AgentJsonContext.Default.InfraIncidentFacts).Should().Be(json);
    }

    [Theory]
    [InlineData("\"tick_ms\"")]
    [InlineData("9")]
    public void TuningKeyConverter_UnknownName_Throws(string keyToken)
    {
        var json = "{\"id\":\"set-1\",\"key\":" + keyToken + ",\"current_value\":1}";

        var act = () => JsonSerializer.Deserialize<SettingFact>(json, AgentJsonContext.Default.Options);

        act.Should().Throw<JsonException>();
    }
}
