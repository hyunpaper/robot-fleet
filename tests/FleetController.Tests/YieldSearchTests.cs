using FluentAssertions;
using RobotFleet.FleetController.Arbitration;
using RobotFleet.Shared.Geometry;
using RobotFleet.Shared.Map;
using RobotFleet.Shared.Tests;
using Xunit;

namespace RobotFleet.FleetController.Tests;

/// DL-04. 13 §4
public sealed class YieldSearchTests
{
    private static Func<Point, bool> OffAisle(GridMap map) => c => map.IsWalkable(c) && c.Y != 0;

    [Fact]
    public void 가장_가까운_회피_칸을_찾는다()
    {
        // 111111 / 001000 — (2,0)에서 포켓 (2,1)이 한 칸 아래
        var map = TestMaps.PocketCorridor();
        var (point, searched) = YieldSearch.Find(map, new(2, 0), 5,
            traversable: map.IsWalkable, isGoal: OffAisle(map));

        point.Should().Be(new Point(2, 1));
        searched.Should().Be(1);
    }

    [Fact]
    public void 통로_칸은_지나되_끝점은_통로_밖이어야()
    {
        // 11111111 / 00100100 — (4,0)에서 통로를 따라 (5,0) 지나 포켓 (5,1)로(두 칸, (2,1)보다 가깝다)
        var map = TestMaps.TwoPocketCorridor();
        var (point, searched) = YieldSearch.Find(map, new(4, 0), 5,
            traversable: map.IsWalkable, isGoal: OffAisle(map));

        point.Should().Be(new Point(5, 1));
        searched.Should().Be(2);
    }

    [Fact]
    public void 점유_칸은_통과하지_못한다()
    {
        // 1111111 / 0010100 — 포켓 (2,1)·(4,1). (3,0) 점유라 동쪽이 막혀 서쪽 포켓만 남는다
        var map = TestMaps.Unchecked("1111111", "0010100");
        var occupied = new HashSet<Point> { new(3, 0) };
        var (point, _) = YieldSearch.Find(map, new(2, 0), 5,
            traversable: c => map.IsWalkable(c) && !occupied.Contains(c), isGoal: OffAisle(map));

        point.Should().Be(new Point(2, 1));
    }

    [Fact]
    public void 상한_안에_없으면_null과_상한()
    {
        // 한 줄 통로 — 통로 밖 칸이 없다
        var map = TestMaps.Open(5, 1);
        var (point, searched) = YieldSearch.Find(map, new(2, 0), 3,
            traversable: map.IsWalkable, isGoal: OffAisle(map));

        point.Should().BeNull();
        searched.Should().Be(3);
    }
}
