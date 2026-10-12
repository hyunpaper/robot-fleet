using FluentAssertions;
using RobotFleet.Shared.Geometry;
using Xunit;

namespace RobotFleet.Shared.Tests;

public class GridMapTests
{
    [Fact] // S-15
    public void WalkableNeighbors_OrderIsNESW()
    {
        var map = TestMaps.Open(5, 5);
        map.WalkableNeighbors(new Point(2, 2)).Should().Equal(
            new Point(2, 1), new Point(3, 2), new Point(2, 3), new Point(1, 2));
    }

    [Fact] // S-16
    public void WalkableNeighbors_ExcludesOutOfBounds()
    {
        var map = TestMaps.Open(5, 5);
        map.WalkableNeighbors(new Point(0, 0)).Should().Equal(new Point(1, 0), new Point(0, 1));
    }

    [Fact]
    public void Indexer_OutOfRange_Throws()
    {
        var map = TestMaps.Open(5, 5);
        var act = () => map[5, 0];
        act.Should().Throw<IndexOutOfRangeException>();
        map.InBounds(new Point(-1, 0)).Should().BeFalse();
    }

    [Fact]
    public void IsWalkable_BlockedCell_False()
    {
        var map = TestMaps.FromRows("101", "111");   // V14 때문에 섬 없이
        map.IsWalkable(new Point(1, 0)).Should().BeFalse();
        map.IsWalkable(new Point(0, 0)).Should().BeTrue();
    }
}
