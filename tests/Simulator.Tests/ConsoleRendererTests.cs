using FluentAssertions;
using RobotFleet.FleetController.Planning;
using RobotFleet.Shared.Domain;
using RobotFleet.Shared.Geometry;
using RobotFleet.Shared.Map;
using Xunit;

namespace RobotFleet.Simulator.Tests;

public class ConsoleRendererTests
{
    private static GridMap OpenMap() =>
        GridMap.FromDefinition(new MapDefinition("t", 1, 5, 5, 1.0, Enumerable.Repeat("11111", 5).ToArray(),
            Array.Empty<StationDef>(), Array.Empty<ChargerDef>(), Array.Empty<ParkingDef>()));

    // T-11 중 로그 포맷 부분. 루프 연동(MOVE 3줄 등)은 SimulationLoop 구현 후 추가
    [Fact]
    public void EventLog_FollowsFixedFormat()
    {
        var writer = new StringWriter();
        var renderer = new ConsoleRenderer(writer, OpenMap(), tickMs: 100, renderEveryTicks: 0);
        var path = new PathResult
        {
            Status = PathStatus.Found,
            Steps = new[] { new PathStep(new Point(0, 0), Heading.East), new PathStep(new Point(1, 0), Heading.East) },
            Cost = 1.0,
            ExpandedNodes = 5,
            Elapsed = TimeSpan.FromMilliseconds(0.8),
        };

        renderer.LogPlan(1, "R-001", new Point(0, 0), Heading.East, new Point(1, 0), path);
        renderer.LogMove(1, "R-001", new Point(0, 0), new Point(1, 0));
        renderer.LogTurn(341, "R-001", Heading.North, Heading.East);
        renderer.LogArrived(11, "R-001", new Point(1, 0), 11, 1, 0, 0);

        var lines = writer.ToString().TrimEnd().Split(Environment.NewLine);
        lines[0].Should().Be("[t=000001 0.1s] R-001 PLAN    (0,0)E → (1,0)   steps=1 cost=1.0 expanded=5 in 0.8ms");
        lines[1].Should().Be("[t=000001 0.1s] R-001 MOVE    (0,0) → (1,0)");
        lines[2].Should().Be("[t=000341 34.1s] R-001 TURN    N → E");
        lines[3].Should().Be("[t=000011 1.1s] R-001 ARRIVED (1,0)   ticks=11 moves=1 turns=0 overrun=0");
    }

    [Fact]
    public void OnTick_RendersWindowOnlyOnInterval()
    {
        var writer = new StringWriter();
        var renderer = new ConsoleRenderer(writer, OpenMap(), 100, renderEveryTicks: 5);
        var robot = new Robot { RobotId = "R-001", MapId = "t", Position = new Point(2, 2), Heading = Heading.East };

        renderer.OnTick(4, robot);
        writer.ToString().Should().BeEmpty();

        renderer.OnTick(5, robot);
        var output = writer.ToString();
        output.Should().Contain(">").And.Contain("tick 000005  pos (2,2) E  Idle  path 0/0");
        output.Split('\n').Count(l => l.StartsWith("  │", StringComparison.Ordinal)).Should().Be(11);
    }
}
