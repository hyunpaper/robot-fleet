using FluentAssertions;
using RobotFleet.Shared.Geometry;
using Xunit;

namespace RobotFleet.Shared.Tests;

public class GeometryTests
{
    [Theory] // S-01
    [InlineData(Heading.North, 5, 4)]
    [InlineData(Heading.East, 6, 5)]
    [InlineData(Heading.South, 5, 6)]
    [InlineData(Heading.West, 4, 5)]
    public void Step_MovesOneCell(Heading h, int x, int y) =>
        new Point(5, 5).Step(h).Should().Be(new Point(x, y));

    [Theory] // S-02
    [InlineData(Heading.North, Heading.North, 0)]
    [InlineData(Heading.North, Heading.East, 1)]
    [InlineData(Heading.North, Heading.West, 1)]
    [InlineData(Heading.North, Heading.South, 2)]
    [InlineData(Heading.East, Heading.West, 2)]
    [InlineData(Heading.West, Heading.North, 1)]
    public void TurnSteps_CountsQuarterTurns(Heading from, Heading to, int expected) =>
        from.TurnSteps(to).Should().Be(expected);

    [Fact] // S-03
    public void HeadingTo_AdjacentCells()
    {
        HeadingExtensions.HeadingTo(new Point(3, 3), new Point(3, 2)).Should().Be(Heading.North);
        HeadingExtensions.HeadingTo(new Point(3, 3), new Point(4, 3)).Should().Be(Heading.East);
        var act = () => HeadingExtensions.HeadingTo(new Point(3, 3), new Point(5, 3));
        act.Should().Throw<ArgumentException>();
    }
}
