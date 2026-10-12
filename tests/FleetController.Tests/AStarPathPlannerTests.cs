using System.Diagnostics;
using FluentAssertions;
using RobotFleet.FleetController.Planning;
using RobotFleet.Shared.Geometry;
using RobotFleet.Shared.Map;
using RobotFleet.Shared.Tests;
using Xunit;
using Xunit.Abstractions;

namespace RobotFleet.FleetController.Tests;

public class AStarPathPlannerTests
{
    private static readonly GridMap Corridor = MapLoader.LoadFromFile(MapPath("test-corridor.json"));
    private static readonly GridMap FactoryA = MapLoader.LoadFromFile(MapPath("factory-a.json"));
    private static readonly GridMap Open5 = TestMaps.Open(5, 5);

    private readonly ITestOutputHelper _output;

    public AStarPathPlannerTests(ITestOutputHelper output) => _output = output;

    private static string MapPath(string name) => Path.Combine(AppContext.BaseDirectory, "maps", name);

    private static PathResult Plan(GridMap map, int sx, int sy, Heading h, int gx, int gy, PathCost? cost = null) =>
        new AStarPathPlanner(cost).FindPath(map, new Point(sx, sy), h, new Point(gx, gy));

    /// <summary>문서 표기 "(x,y)H (x,y)H ...".</summary>
    private static string Format(PathResult r) => string.Join(" ", r.Steps.Select(s => $"{s.Cell}{s.Heading.ToChar()}"));

    private static int TurnCount(PathResult r) =>
        Enumerable.Range(1, r.Steps.Count - 1).Count(i => r.Steps[i].Heading != r.Steps[i - 1].Heading);

    [Fact] // P-01
    public void Straight()
    {
        var r = Plan(Corridor, 0, 0, Heading.East, 8, 0);

        r.Status.Should().Be(PathStatus.Found);
        r.Cost.Should().Be(8.0);
        r.Steps.Should().HaveCount(9).And.OnlyContain(s => s.Heading == Heading.East);
    }

    [Fact] // P-02
    public void PrefersSingleTurn()
    {
        var r = Plan(Open5, 0, 0, Heading.East, 4, 4);

        r.Status.Should().Be(PathStatus.Found);
        r.Cost.Should().Be(8.5);
        Format(r).Should().Be("(0,0)E (1,0)E (2,0)E (3,0)E (4,0)E (4,1)S (4,2)S (4,3)S (4,4)S");
    }

    [Fact] // P-03
    public void InitialTurn_WhenStartHeadingDiffers()
    {
        var r = Plan(Open5, 0, 0, Heading.North, 4, 4);

        r.Cost.Should().Be(9.0);
        Format(r).Should().Be("(0,0)N (1,0)E (2,0)E (3,0)E (4,0)E (4,1)S (4,2)S (4,3)S (4,4)S");
    }

    [Fact] // P-04
    public void UTurn()
    {
        var r = Plan(Open5, 2, 0, Heading.West, 4, 0);

        r.Cost.Should().Be(3.0);
        Format(r).Should().Be("(2,0)W (3,0)E (4,0)E");
    }

    [Fact] // P-05
    public void ThroughSingleCellPassage()
    {
        var r = Plan(Corridor, 1, 1, Heading.South, 7, 5);

        r.Cost.Should().Be(12.0);
        r.Steps.Should().HaveCount(11);
        Format(r).Should().Be("(1,1)S (1,2)S (2,2)E (3,2)E (4,2)E (4,3)S (4,4)S (5,4)E (6,4)E (7,4)E (7,5)S");
    }

    [Fact] // P-06
    public void NoPath()
    {
        var map = TestMaps.Unchecked("11011", "11011", "11011");

        var r = Plan(map, 0, 0, Heading.East, 4, 0);

        r.Status.Should().Be(PathStatus.NoPath);
        r.Steps.Should().BeEmpty();
        r.ExpandedNodes.Should().BeInRange(1, 24);
    }

    [Fact] // P-07
    public void AlreadyAtGoal()
    {
        var r = Plan(Open5, 2, 2, Heading.East, 2, 2);

        r.Status.Should().Be(PathStatus.AlreadyAtGoal);
        r.Steps.Should().Equal(new PathStep(new Point(2, 2), Heading.East));
        r.Cost.Should().Be(0);
        r.MoveCount.Should().Be(0);
    }

    [Theory]
    [InlineData(3, 3, 0, 0, PathStatus.InvalidStart)]    // P-08 시작점 막힘
    [InlineData(-1, 0, 0, 0, PathStatus.InvalidStart)]   // P-09 시작점 범위 밖
    [InlineData(0, 0, 3, 3, PathStatus.InvalidGoal)]     // P-10 목적지 막힘
    [InlineData(0, 0, 9, 0, PathStatus.InvalidGoal)]     // P-11 목적지 범위 밖
    [InlineData(3, 3, 2, 3, PathStatus.InvalidStart)]    // P-12 둘 다 무효면 시작 먼저
    public void InvalidEndpoints(int sx, int sy, int gx, int gy, PathStatus expected)
    {
        var r = Plan(Corridor, sx, sy, Heading.North, gx, gy);

        r.Status.Should().Be(expected);
        r.Steps.Should().BeEmpty();
        r.IsSuccess.Should().BeFalse();
    }

    [Fact] // P-13
    public void ZeroTurnCost_AllowsZigzag()
    {
        var r = Plan(Open5, 0, 0, Heading.East, 4, 4, new PathCost(TurnPer90: 0));

        r.Cost.Should().Be(8.0);
        r.MoveCount.Should().Be(8);
    }

    [Fact] // P-14
    public void StraightCorridor_NotDetour()
    {
        var r = Plan(Corridor, 0, 2, Heading.East, 8, 2);

        r.Cost.Should().Be(8.0);
        r.Steps.Should().OnlyContain(s => s.Cell.Y == 2 && s.Heading == Heading.East);
    }

    public static IEnumerable<object[]> ParkingStationPairs() =>
        from p in FactoryA.Parkings
        from s in FactoryA.Stations
        select new object[] { p.Id, s.Id };

    [Theory] // P-15, P-16
    [MemberData(nameof(ParkingStationPairs))]
    public void FactoryA_ParkingToStation_IsValidPath(string parkingId, string stationId)
    {
        var p = FactoryA.FindParking(parkingId)!;
        var s = FactoryA.FindStation(stationId)!;
        var start = new Point(p.X, p.Y);
        var goal = new Point(s.X, s.Y);

        var r = new AStarPathPlanner().FindPath(FactoryA, start, Heading.North, goal);

        r.Status.Should().Be(PathStatus.Found);
        r.Steps[0].Should().Be(new PathStep(start, Heading.North));
        r.Steps[^1].Cell.Should().Be(goal);
        r.Steps.Should().OnlyContain(st => FactoryA.IsWalkable(st.Cell));

        var cost = PathCost.Default;
        var recomputed = 0.0;
        for (var i = 1; i < r.Steps.Count; i++)
        {
            var (prev, cur) = (r.Steps[i - 1], r.Steps[i]);
            prev.Cell.ManhattanTo(cur.Cell).Should().Be(1);
            cur.Heading.Should().Be(HeadingExtensions.HeadingTo(prev.Cell, cur.Cell));
            recomputed += cost.Move + cost.Turn(prev.Heading, cur.Heading);
        }
        r.Cost.Should().BeApproximately(recomputed, 1e-9);
        r.Cost.Should().BeGreaterThanOrEqualTo(start.ManhattanTo(goal) * 1.0);   // P-16
    }

    [Fact] // P-17
    public void FactoryA_DefaultScenario()
    {
        var r = Plan(FactoryA, 0, 39, Heading.North, 6, 3);

        r.Cost.Should().Be(43.0);
        r.Steps.Should().HaveCount(43);
        TurnCount(r).Should().Be(2);
        r.Steps[34].Should().Be(new PathStep(new Point(0, 5), Heading.North));   // 06 콘솔 예시: (0,5)에서 N → E
        r.Steps[35].Should().Be(new PathStep(new Point(1, 5), Heading.East));
        r.Steps[^1].Cell.Should().Be(new Point(6, 3));
    }

    [Fact] // P-18
    public void FactoryA_Performance()
    {
        var planner = new AStarPathPlanner();
        var start = new Point(0, 39);
        var goal = new Point(59, 0);
        for (var i = 0; i < 5; i++) planner.FindPath(FactoryA, start, Heading.North, goal);   // JIT 워밍업

        const int runs = 100;
        var sw = Stopwatch.StartNew();
        PathResult r = null!;
        for (var i = 0; i < runs; i++) r = planner.FindPath(FactoryA, start, Heading.North, goal);
        sw.Stop();

        var avgMs = sw.Elapsed.TotalMilliseconds / runs;
        _output.WriteLine($"avg {avgMs:F3}ms, expanded {r.ExpandedNodes}, cost {r.Cost}");
        r.Status.Should().Be(PathStatus.Found);
#if DEBUG
        avgMs.Should().BeLessThan(10);
#else
        avgMs.Should().BeLessThan(2);
#endif
    }

    [Fact] // P-19
    public void Deterministic()
    {
        var a = Plan(FactoryA, 0, 39, Heading.North, 6, 3);
        var b = Plan(FactoryA, 0, 39, Heading.North, 6, 3);

        b.Steps.Should().Equal(a.Steps);
    }
}
