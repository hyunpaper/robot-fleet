using FluentAssertions;
using RobotFleet.Shared.Tuning;
using Xunit;

namespace RobotFleet.Shared.Tests.Tuning;

public class TuningCatalogTests
{
    // 04 "## 설정값" 표를 그대로 옮긴 값: Key, Name, Kind, Default, Min, Max, MaxStep
    private static readonly (TuningKey Key, string Name, TuningValueKind Kind, double Def, double Min, double Max, double Step)[] Table =
    [
        (TuningKey.CongestionAlpha, "congestion_alpha", TuningValueKind.Decimal, 0, 0, 2.0, 0.3),
        (TuningKey.TurnCost, "turn_cost", TuningValueKind.Decimal, 0.5, 0, 2.0, 0.25),
        (TuningKey.PriorityWaitWeight, "priority.wait_weight", TuningValueKind.Decimal, 0.01, 0, 0.1, 0.01),
        (TuningKey.PriorityDistanceWeight, "priority.distance_weight", TuningValueKind.Decimal, 1.0, 0, 3.0, 0.5),
        (TuningKey.PriorityTaskPriorityWeight, "priority.task_priority_weight", TuningValueKind.Decimal, 50, 0, 200, 25),
        (TuningKey.ReplanWaitThresholdMs, "replan_wait_threshold_ms", TuningValueKind.Integer, 500, 200, 2000, 250),
        (TuningKey.ReplanCooldownMs, "replan_cooldown_ms", TuningValueKind.Integer, 100, 50, 1000, 100),
        (TuningKey.ReplanMaxPerTask, "replan_max_per_task", TuningValueKind.Integer, 5, 3, 10, 2),
        (TuningKey.DeadlockDetectWaitMs, "deadlock_detect_wait_ms", TuningValueKind.Integer, 1000, 500, 3000, 500),
        (TuningKey.YieldSearchMaxDistance, "yield_search_max_distance", TuningValueKind.Integer, 5, 3, 15, 3),
        (TuningKey.YieldCostDistanceWeight, "yield_cost.distance_weight", TuningValueKind.Decimal, 1.0, 0, 3.0, 0.5),
        (TuningKey.YieldCostPriorityWeight, "yield_cost.priority_weight", TuningValueKind.Decimal, 1.0, 0, 3.0, 0.5),
        (TuningKey.DeadlockResolveTtlTicks, "deadlock_resolve_ttl_ticks", TuningValueKind.Integer, 50, 30, 120, 20),
    ];

    [Fact] // TC-01
    public void TuningCatalog_Matches04Table()
    {
        TuningCatalog.All.Should().HaveCount(13);
        for (var i = 0; i < Table.Length; i++)
        {
            var e = Table[i];
            var p = TuningCatalog.All[i];
            ((int)p.Key).Should().Be(i + 1);
            p.Key.Should().Be(e.Key);
            p.Name.Should().Be(e.Name);
            p.Kind.Should().Be(e.Kind);
            p.Default.Should().Be(e.Def);
            p.Min.Should().Be(e.Min);
            p.Max.Should().Be(e.Max);
            p.MaxStep.Should().Be(e.Step);
            TuningCatalog.Get(e.Key).Should().BeSameAs(p);
        }

        TuningCatalog.All.Select(p => p.Name).Distinct().Should().HaveCount(13);
    }

    [Fact] // TC-02
    public void TuningCatalog_ValidateRange_BoundsInclusive()
    {
        foreach (var p in TuningCatalog.All)
        {
            TuningCatalog.ValidateRange(p.Key, p.Min);
            TuningCatalog.ValidateRange(p.Key, p.Max);

            var below = () => TuningCatalog.ValidateRange(p.Key, p.Min - 1);
            below.Should().Throw<ArgumentException>().WithMessage("*범위*");
            var above = () => TuningCatalog.ValidateRange(p.Key, p.Max + 1);
            above.Should().Throw<ArgumentException>().WithMessage("*범위*");
        }

        var notInt = () => TuningCatalog.ValidateRange(TuningKey.YieldSearchMaxDistance, 7.5);
        notInt.Should().Throw<ArgumentException>().WithMessage("*정수*");
    }

    [Theory] // TC-03
    [InlineData(5, 50)]
    [InlineData(7, 70)]
    public void TuningCatalog_Coupling_TtlAtLeastDistanceTimes10_Passes(double distance, double ttl)
    {
        var act = () => TuningCatalog.ValidateCouplings(Values(distance, ttl));

        act.Should().NotThrow();
    }

    [Theory] // TC-03
    [InlineData(5, 49)]
    [InlineData(8, 70)]
    public void TuningCatalog_Coupling_TtlAtLeastDistanceTimes10_Throws(double distance, double ttl)
    {
        var act = () => TuningCatalog.ValidateCouplings(Values(distance, ttl));

        act.Should().Throw<ArgumentException>()
            .WithMessage("*deadlock_resolve_ttl_ticks*")
            .WithMessage("*yield_search_max_distance*");
    }

    [Fact] // TC-04
    public void TuningCatalog_TryParseName()
    {
        foreach (var p in TuningCatalog.All)
        {
            TuningCatalog.TryParseName(p.Name, out var key).Should().BeTrue();
            key.Should().Be(p.Key);
        }

        TuningCatalog.TryParseName("tick_ms", out _).Should().BeFalse();
        TuningCatalog.TryParseName("Turn_Cost", out _).Should().BeFalse();
    }

    private static Func<TuningKey, double> Values(double distance, double ttl)
        => k => k switch
        {
            TuningKey.YieldSearchMaxDistance => distance,
            TuningKey.DeadlockResolveTtlTicks => ttl,
            _ => TuningCatalog.Get(k).Default,
        };
}
