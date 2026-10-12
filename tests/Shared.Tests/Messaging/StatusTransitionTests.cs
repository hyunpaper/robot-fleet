using FluentAssertions;
using RobotFleet.Shared.Domain;
using Xunit;

namespace RobotFleet.Shared.Tests.Messaging;

public class StatusTransitionTests
{
    [Fact]
    public void All_ContainsEveryCellOfTheTable()
    {
        StatusTransitions.All.Should().HaveCount(24);
        StatusTransitions.All.Distinct().Should().HaveCount(24);
    }

    [Fact]
    public void AllowedCells_AreAllAllowed()
    {
        foreach (var (from, to, reason) in StatusTransitions.All)
        {
            StatusTransitions.IsAllowed(from, to, reason).Should().BeTrue($"{from}->{to} {reason}");
        }
    }

    [Theory]
    [InlineData(RobotStatus.Idle, RobotStatus.Loading, "task_assigned")]
    [InlineData(RobotStatus.Loading, RobotStatus.Unloading, "arrived_destination")]
    [InlineData(RobotStatus.Charging, RobotStatus.Moving, "load_done")]
    [InlineData(RobotStatus.Waiting, RobotStatus.Charging, "arrived_charger")]
    [InlineData(RobotStatus.Error, RobotStatus.Moving, "path_cleared")]
    public void ForbiddenExamples_AreRejected(RobotStatus from, RobotStatus to, string reason)
    {
        StatusTransitions.IsAllowed(from, to, reason).Should().BeFalse();
    }

    [Fact]
    public void WrongReason_ForAllowedPair_IsRejected()
    {
        StatusTransitions.IsAllowed(RobotStatus.Idle, RobotStatus.Moving, "conflict_wait").Should().BeFalse();
    }

    [Fact]
    public void M3Lifecycle_IsAllowed()
    {
        StatusTransitions.IsAllowed(RobotStatus.Idle, RobotStatus.Moving, "task_assigned").Should().BeTrue();
        StatusTransitions.IsAllowed(RobotStatus.Moving, RobotStatus.Loading, "arrived_origin").Should().BeTrue();
        StatusTransitions.IsAllowed(RobotStatus.Loading, RobotStatus.Moving, "load_done").Should().BeTrue();
        StatusTransitions.IsAllowed(RobotStatus.Moving, RobotStatus.Unloading, "arrived_destination").Should().BeTrue();
        StatusTransitions.IsAllowed(RobotStatus.Unloading, RobotStatus.Idle, "unload_done").Should().BeTrue();
        StatusTransitions.IsAllowed(RobotStatus.Moving, RobotStatus.Error, "no_path").Should().BeTrue();
    }
}
