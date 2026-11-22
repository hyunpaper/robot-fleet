using FluentAssertions;
using RobotFleet.FleetController.Arbitration;
using RobotFleet.FleetController.Planning;
using RobotFleet.Shared.Tuning;
using RobotFleet.Simulator.Fleet;
using Xunit;

namespace RobotFleet.Simulator.Tests;

public class OptionDefaultsTests
{
    [Fact] // TC-05
    public void OptionDefaults_EqualCatalogDefaults()
    {
        var d = new DeadlockOptions();
        var f = new FleetOptions();

        d.DeadlockDetectWaitMs.Should().Be((int)Def(TuningKey.DeadlockDetectWaitMs));
        d.YieldSearchMaxDistance.Should().Be((int)Def(TuningKey.YieldSearchMaxDistance));
        d.YieldDistanceWeight.Should().Be(Def(TuningKey.YieldCostDistanceWeight));
        d.YieldPriorityWeight.Should().Be(Def(TuningKey.YieldCostPriorityWeight));
        d.DeadlockResolveTtlTicks.Should().Be((int)Def(TuningKey.DeadlockResolveTtlTicks));
        f.ReplanWaitThresholdMs.Should().Be((int)Def(TuningKey.ReplanWaitThresholdMs));
        f.ReplanCooldownMs.Should().Be((int)Def(TuningKey.ReplanCooldownMs));
        f.ReplanMaxPerTask.Should().Be((int)Def(TuningKey.ReplanMaxPerTask));
        PathCost.Default.TurnPer90.Should().Be(Def(TuningKey.TurnCost));
    }

    private static double Def(TuningKey key) => TuningCatalog.Get(key).Default;
}
