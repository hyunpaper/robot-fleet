using FluentAssertions;
using RobotFleet.FleetController.Arbitration;
using Xunit;

namespace RobotFleet.FleetController.Tests;

public class DeadlockOptionsTests
{
    [Fact] // DL-06
    public void Validate_Defaults_Pass()
    {
        var act = () => new DeadlockOptions().Validate();

        act.Should().NotThrow();
    }

    [Theory] // DL-06
    [InlineData(5, 50)]
    [InlineData(7, 70)]
    [InlineData(3, 120)]
    public void Validate_TtlAtOrAboveDistanceTimes10_Passes(int distance, int ttl)
    {
        var options = new DeadlockOptions { YieldSearchMaxDistance = distance, DeadlockResolveTtlTicks = ttl };

        var act = () => options.Validate();

        act.Should().NotThrow();
    }

    [Theory] // DL-06
    [InlineData(5, 49)]
    [InlineData(8, 70)]
    [InlineData(15, 120)]
    public void Validate_TtlBelowDistanceTimes10_Throws(int distance, int ttl)
    {
        var options = new DeadlockOptions { YieldSearchMaxDistance = distance, DeadlockResolveTtlTicks = ttl };

        var act = () => options.Validate();

        act.Should().Throw<ArgumentException>().WithMessage("*deadlock_resolve_ttl_ticks*");
    }

    [Theory] // DL-06 — 04 허용 범위
    [InlineData(nameof(DeadlockOptions.DeadlockDetectWaitMs))]
    [InlineData(nameof(DeadlockOptions.YieldSearchMaxDistance))]
    [InlineData(nameof(DeadlockOptions.YieldDistanceWeight))]
    [InlineData(nameof(DeadlockOptions.DeadlockResolveTtlTicks))]
    public void Validate_OutOfAllowedRange_Throws(string which)
    {
        var options = which switch
        {
            nameof(DeadlockOptions.DeadlockDetectWaitMs) => new DeadlockOptions { DeadlockDetectWaitMs = 100 },      // < 500
            nameof(DeadlockOptions.YieldSearchMaxDistance) => new DeadlockOptions { YieldSearchMaxDistance = 20, DeadlockResolveTtlTicks = 120 },   // > 15
            nameof(DeadlockOptions.YieldDistanceWeight) => new DeadlockOptions { YieldDistanceWeight = 5.0 },        // > 3.0
            _ => new DeadlockOptions { DeadlockResolveTtlTicks = 200 },                                              // > 120
        };

        var act = () => options.Validate();

        act.Should().Throw<ArgumentException>().WithMessage("*범위*");
    }
}
