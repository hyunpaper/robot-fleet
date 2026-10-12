using FluentAssertions;
using RobotFleet.FleetController.Arbitration;
using RobotFleet.Shared.Geometry;
using Xunit;

namespace RobotFleet.FleetController.Tests;

public class AlwaysGrantMoveArbiterTests
{
    [Fact] // A-01
    public async Task Decide_GrantsAll_PreservingOrder()
    {
        var requests = new[]
        {
            new MoveRequest("R-001", new Point(0, 0), new Point(1, 0), Heading.East, 1),
            new MoveRequest("R-002", new Point(0, 1), new Point(1, 1), Heading.East, 1),
            new MoveRequest("R-003", new Point(0, 2), new Point(1, 2), Heading.East, 1),
        };

        var decisions = await new AlwaysGrantMoveArbiter().DecideAsync("m", 1, requests);

        decisions.Should().HaveCount(3);
        decisions.Should().OnlyContain(d => d.Granted && d.Reason == MoveDenyReason.None);
        decisions.Select(d => d.RobotId).Should().Equal("R-001", "R-002", "R-003");
    }

    [Fact] // A-02
    public async Task Decide_EmptyBatch_ReturnsEmpty()
    {
        var decisions = await new AlwaysGrantMoveArbiter().DecideAsync("m", 1, Array.Empty<MoveRequest>());
        decisions.Should().BeEmpty();
    }
}
