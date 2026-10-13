using FluentAssertions;
using RobotFleet.FleetController.Arbitration;
using RobotFleet.FleetController.Planning;
using RobotFleet.Shared.Domain;
using RobotFleet.Shared.Geometry;
using RobotFleet.Shared.Map;
using RobotFleet.Shared.Tests;
using Xunit;
using TaskStatus = RobotFleet.Shared.Domain.TaskStatus;

namespace RobotFleet.Simulator.Tests;

public class MultiRobotTests
{
    private sealed class Fleet
    {
        public required RobotStepper Stepper { get; init; }
        public required InMemoryMoveArbiter Arbiter { get; init; }
        public required List<(Robot Robot, TransportTask Task)> Units { get; init; }
        public required StringWriter Log { get; init; }
        public long Tick { get; private set; }

        public IReadOnlyList<Robot> Robots => Units.Select(u => u.Robot).ToList();

        /// <summary>전원 도착할 때까지(또는 maxTicks) 돌리며 매 틱 끝에 점유 불변식을 확인한다.</summary>
        public async Task RunAsync(int maxTicks = 500)
        {
            while (Tick < maxTicks && Units.Any(u => u.Task.Status != TaskStatus.Completed))
            {
                await StepAsync();
            }
        }

        public async Task StepAsync()
        {
            Tick++;
            await Stepper.StepAllAsync(Robots, Tick);
            AssertNoSharedCell();
        }

        private void AssertNoSharedCell()
        {
            Robots.Select(r => r.Position).Should().OnlyHaveUniqueItems($"tick {Tick}: 같은 칸에 두 로봇");

            // 이동 중인 로봇은 출발·도착 2칸을 차지한다
            var claimed = Robots.SelectMany(r => Stepper.CurrentAction(r) is { Kind: RobotActionKind.Move } a
                ? new[] { r.Position, a.TargetCell }
                : new[] { r.Position });
            claimed.Should().OnlyHaveUniqueItems($"tick {Tick}: 점유 칸이 겹침");
        }

        public string[] LogLines(string robotId, string kind) => Log.ToString().Split(Environment.NewLine)
            .Where(l => l.Contains($" {robotId} {kind}", StringComparison.Ordinal)).ToArray();
    }

    private static Fleet Create(GridMap map, params (string Id, Point Start, Heading Heading, Point Goal)[] specs)
    {
        var options = new SimulationOptions { MapPath = "test", Goal = default, SpeedFactor = 0 };
        var log = new StringWriter();
        var arbiter = new InMemoryMoveArbiter();
        var stepper = new RobotStepper(map, new AStarPathPlanner(), arbiter, options, new SimulationStats(),
            new ConsoleRenderer(log, map, options.TickMs, 0));
        var units = new List<(Robot, TransportTask)>();
        foreach (var (id, start, heading, goal) in specs)
        {
            var robot = new Robot { RobotId = id, MapId = map.MapId, Position = start, Heading = heading };
            var task = new TransportTask { TaskId = "T-" + id, MapId = map.MapId, Type = TaskType.Park, Origin = start, Destination = goal };
            arbiter.Register(map.MapId, id, start);
            stepper.Assign(robot, task);
            units.Add((robot, task));
        }
        return new Fleet { Stepper = stepper, Arbiter = arbiter, Units = units, Log = log };
    }

    [Fact]
    public async Task Intersection_OneWaitsThenBothArrive()
    {
        // 두 로봇이 tick 11에 동시에 (2,2)를 요청. 잔여 칸 수가 같아 R-001이 먼저
        var fleet = Create(TestMaps.Open(5, 5),
            ("R-001", new(0, 2), Heading.East, new(4, 2)),
            ("R-002", new(2, 0), Heading.South, new(2, 4)));

        await fleet.RunAsync();

        fleet.Units.Should().OnlyContain(u => u.Task.Status == TaskStatus.Completed);
        fleet.Units[0].Robot.Position.Should().Be(new Point(4, 2));
        fleet.Units[1].Robot.Position.Should().Be(new Point(2, 4));

        fleet.LogLines("R-001", "WAIT").Should().BeEmpty();
        fleet.LogLines("R-002", "WAIT").Should().Equal("[t=000011 1.1s] R-002 WAIT    (2,1)   reason=Occupied");
        // R-001이 (2,2)를 떠나 tick 30에 해제한 다음 틱에 진입
        fleet.LogLines("R-002", "MOVE").Should().Contain("[t=000031 3.1s] R-002 MOVE    (2,1) → (2,2)");
        fleet.LogLines("R-001", "ARRIVED").Should().ContainSingle().Which.Should().Contain("t=000041");
        fleet.LogLines("R-002", "ARRIVED").Should().ContainSingle().Which.Should().Contain("t=000061");
    }

    [Fact]
    public async Task Following_WaitsForLeaderToLeaveCell()
    {
        // 1칸 통로에서 같은 방향. 뒤 로봇은 앞 로봇의 출발 칸에 못 들어간다(2칸 선점)
        var map = TestMaps.FromRows("111111");
        var fleet = Create(map,
            ("R-001", new(1, 0), Heading.East, new(4, 0)),
            ("R-002", new(0, 0), Heading.East, new(3, 0)));
        var follower = fleet.Units[1].Robot;

        await fleet.StepAsync();
        follower.Status.Should().Be(RobotStatus.Waiting);
        fleet.Arbiter.OccupantOf(map.MapId, new(1, 0)).Should().Be("R-001");
        fleet.Arbiter.OccupantOf(map.MapId, new(2, 0)).Should().Be("R-001");

        for (var i = 0; i < 9; i++) await fleet.StepAsync();      // tick 10: 앞 로봇 (2,0) 도착, (1,0) 해제
        follower.Status.Should().Be(RobotStatus.Waiting);
        fleet.Arbiter.OccupantOf(map.MapId, new(1, 0)).Should().BeNull();

        await fleet.StepAsync();                                   // tick 11: 재요청 승인
        follower.Status.Should().Be(RobotStatus.Moving);
        fleet.LogLines("R-002", "MOVE").Should().Equal("[t=000011 1.1s] R-002 MOVE    (0,0) → (1,0)");

        await fleet.RunAsync();
        fleet.Units.Should().OnlyContain(u => u.Task.Status == TaskStatus.Completed);
        fleet.LogLines("R-002", "WAIT").Should().ContainSingle("Waiting이 이어지는 동안 WAIT는 한 번만 찍는다");
    }

    [Fact]
    public async Task HeadOn_InCorridor_BothStayWaiting()
    {
        // 맞바꿈은 대기만으로 풀리지 않는다. 교착 해소(양보)는 이후 단계
        var fleet = Create(TestMaps.FromRows("1111"),
            ("R-001", new(1, 0), Heading.East, new(3, 0)),
            ("R-002", new(2, 0), Heading.West, new(0, 0)));

        await fleet.RunAsync(maxTicks: 30);

        fleet.Robots.Should().OnlyContain(r => r.Status == RobotStatus.Waiting);
        fleet.LogLines("R-001", "WAIT").Should().Equal("[t=000001 0.1s] R-001 WAIT    (1,0)   reason=Occupied");
        fleet.LogLines("R-002", "WAIT").Should().Equal("[t=000001 0.1s] R-002 WAIT    (2,0)   reason=Swap");
    }

    [Fact]
    public async Task StepAll_CallsDecideOncePerTick()
    {
        var arbiter = new CountingArbiter(new InMemoryMoveArbiter());
        var map = TestMaps.Open(5, 5);
        var options = new SimulationOptions { MapPath = "test", Goal = default, SpeedFactor = 0 };
        var stepper = new RobotStepper(map, new AStarPathPlanner(), arbiter, options, new SimulationStats(), null);
        var robots = Enumerable.Range(0, 3).Select(y =>
        {
            var robot = new Robot { RobotId = $"R-00{y}", MapId = map.MapId, Position = new(0, y), Heading = Heading.East };
            stepper.Assign(robot, new TransportTask { TaskId = $"T-{y}", MapId = map.MapId, Type = TaskType.Park, Origin = robot.Position, Destination = new(2, y) });
            return robot;
        }).ToList();

        await stepper.StepAllAsync(robots, 1);

        arbiter.Batches.Should().ContainSingle().Which.Should().Be(3);
    }

    private sealed class CountingArbiter(IMoveArbiter inner) : IMoveArbiter
    {
        public List<int> Batches { get; } = new();

        public ValueTask<IReadOnlyList<MoveDecision>> DecideAsync(
            string mapId, long tick, IReadOnlyList<MoveRequest> requests, CancellationToken ct = default)
        {
            Batches.Add(requests.Count);
            return inner.DecideAsync(mapId, tick, requests, ct);
        }

        public ValueTask ReleaseAsync(string mapId, string robotId, Point cell, CancellationToken ct = default)
            => inner.ReleaseAsync(mapId, robotId, cell, ct);

        public IReadOnlyList<DeadlockInfo> DrainPendingDeadlocks(string mapId, long tick)
            => inner.DrainPendingDeadlocks(mapId, tick);
    }
}
