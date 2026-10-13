using FluentAssertions;
using RobotFleet.FleetController.Arbitration;
using RobotFleet.FleetController.Planning;
using RobotFleet.Shared.Domain;
using RobotFleet.Shared.Geometry;
using RobotFleet.Shared.Map;
using RobotFleet.Shared.Messaging;
using RobotFleet.Shared.Pipeline;
using RobotFleet.Shared.Tests;
using RobotFleet.Simulator.Events;
using RobotFleet.Simulator.Fleet;
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

    // 교착 탐지·해소를 켠 배선(DeadlockOptions + 맵 등록 + TaskLifecycle 관찰자 + EventCollector). WorkMs=0으로 체류 즉시
    private static async Task<(IReadOnlyList<TransportTask> Tasks, IReadOnlyList<Robot> Robots, List<EventItem> Events)> RunDeadlock(
        GridMap map, int maxTicks, params (string Id, Point Start, Heading Heading, Point Goal)[] specs)
    {
        var options = new FleetOptions { WorkMs = 0, TickMs = 100, SpeedFactor = 0, Robots = specs.Length };
        var planner = new AStarPathPlanner();
        var collector = new EventCollector(map.MapId, "r1", options.TickMs, TimeProvider.System);
        var lifecycle = new TaskLifecycle(map, planner, options, collector);
        var arbiter = new InMemoryMoveArbiter(new DeadlockOptions(), options.TickMs);
        arbiter.RegisterMap(map);
        var simOptions = new SimulationOptions { MapPath = "", Goal = default, TickMs = options.TickMs, SpeedFactor = 0 };
        var stepper = new RobotStepper(map, planner, arbiter, simOptions, new SimulationStats(), null, lifecycle);
        lifecycle.Stepper = stepper;

        var tasks = new List<TransportTask>();
        var robots = new List<Robot>();
        foreach (var (id, start, heading, goal) in specs)
        {
            var robot = new Robot { RobotId = id, MapId = map.MapId, Position = start, Heading = heading };
            tasks.Add(new TransportTask { TaskId = "T-" + id, MapId = map.MapId, Type = TaskType.Transport, Origin = start, Destination = goal });
            arbiter.Register(map.MapId, id, start);
            lifecycle.Add(robot);
            robots.Add(robot);
        }

        var events = new List<EventItem>();
        for (long tick = 1; tick <= maxTicks; tick++)
        {
            collector.Begin(tick, DateTimeOffset.UnixEpoch.AddMilliseconds(tick * options.TickMs));
            if (tick == 1)
                for (var i = 0; i < robots.Count; i++) lifecycle.Assign(robots[i], tasks[i], tick);
            lifecycle.AdvanceDwell(tick);
            await stepper.StepAllAsync(robots, tick);
            lifecycle.AfterStep();
            lifecycle.ApplyReplans(tick);
            foreach (var b in collector.Complete(robots.Select(lifecycle.TelemetryOf))) events.AddRange(b.Events);
            if (tasks.All(t => t.Status is TaskStatus.Completed or TaskStatus.Failed)) break;
        }

        return (tasks, robots, events);
    }

    private static IReadOnlyList<DeadlockDetectedPayload> Deadlocks(IEnumerable<EventItem> events) =>
        events.Where(e => e.EventType == EventType.DeadlockDetected)
            .Select(e => StoreMapping.Read<DeadlockDetectedPayload>(e.Payload)).ToList();

    [Fact]
    public async Task DL07_HeadOn_WithPocket_YieldResolvesAndBothArrive()
    {
        // R-001이 포켓 (2,1) 바로 위에서 동쪽, R-002가 (3,0)에서 서쪽 — 정면 대치
        var (tasks, _, events) = await RunDeadlock(TestMaps.PocketCorridor(), 500,
            ("R-001", new(2, 0), Heading.East, new(5, 0)),
            ("R-002", new(3, 0), Heading.West, new(0, 0)));

        tasks.Should().OnlyContain(t => t.Status == TaskStatus.Completed);
        var deadlocks = Deadlocks(events);
        deadlocks.Should().ContainSingle();
        deadlocks[0].Resolution.Should().Be("yield");
        deadlocks[0].SearchedDistance.Should().BeGreaterThan(0);
        deadlocks[0].ResolvedMs.Should().BeGreaterThan(0);
        events.Count(e => e.EventType == EventType.PathReplanned
                          && StoreMapping.Read<PathReplannedPayload>(e.Payload).Reason == "deadlock").Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task DL08_HeadOn_DeadEndCorridor_ResolutionFails()
    {
        // 회피 지점이 없는 1칸 통로
        var (tasks, robots, events) = await RunDeadlock(TestMaps.DeadEndCorridor(), 500,
            ("R-001", new(1, 0), Heading.East, new(3, 0)),
            ("R-002", new(2, 0), Heading.West, new(0, 0)));

        tasks.Should().OnlyContain(t => t.Status == TaskStatus.Failed);
        robots.Should().OnlyContain(r => r.Status == RobotStatus.Error);
        var deadlocks = Deadlocks(events);
        deadlocks.Should().ContainSingle();
        deadlocks[0].Resolution.Should().Be("failed");
        deadlocks[0].ResolvedMs.Should().Be(0);
        deadlocks[0].SearchedDistance.Should().Be(new DeadlockOptions().YieldSearchMaxDistance + 2);
        events.Count(e => e.EventType == EventType.TaskFailed
                          && StoreMapping.Read<TaskFailedPayload>(e.Payload).Reason == "deadlock_unresolved").Should().Be(2);
    }

    [Fact]
    public async Task DL09_ThreeVersusOne_NoPingPongOrStarvation()
    {
        // 동쪽 3대 행렬 대 서쪽 1대. 포켓 (2,1)·(5,1). 1칸 통로라 전원 완료는 불가(완료 로봇이 통로를 막는다) —
        // 핵심은 핑퐁·기아 없음: 해소가 일어나고, 같은 로봇이 무한 양보하지 않으며, 재탐지가 폭주하지 않는다
        var (_, _, events) = await RunDeadlock(TestMaps.TwoPocketCorridor(), 400,
            ("R-001", new(3, 0), Heading.East, new(5, 0)),
            ("R-002", new(2, 0), Heading.East, new(6, 0)),
            ("R-003", new(1, 0), Heading.East, new(7, 0)),
            ("R-004", new(4, 0), Heading.West, new(0, 0)));

        var deadlocks = Deadlocks(events);
        deadlocks.Should().NotBeEmpty("해소가 일어나야 한다");
        deadlocks.Where(d => d.Resolution == "yield")
            .GroupBy(d => d.YieldRobotId).Should().OnlyContain(g => g.Count() <= 3, "같은 로봇이 무한 양보하지 않는다");
        deadlocks.Count.Should().BeLessThan(12, "재탐지 폭주가 없다");
    }

    [Fact]
    public async Task DL10_Blocked_WithAlternative_Reroutes_NotDeadlock()
    {
        // 2행 맵에서 R-002가 (1,0)에 머물러 R-001의 직선 길을 막는다. 대안(아래 행)이 있어 재계획으로 우회한다
        var (tasks, _, events) = await RunDeadlock(TestMaps.Open(3, 2), 200,
            ("R-001", new(0, 0), Heading.East, new(2, 0)),
            ("R-002", new(1, 0), Heading.East, new(1, 0)));   // 제자리 작업 → (1,0) 점유

        tasks[0].Status.Should().Be(TaskStatus.Completed, "대안으로 우회해 도착한다");
        events.Count(e => e.EventType == EventType.PathReplanned
                          && StoreMapping.Read<PathReplannedPayload>(e.Payload).Reason == "blocked").Should().BeGreaterThan(0);
        events.Should().NotContain(e => e.EventType == EventType.DeadlockDetected, "대안이 있으면 교착이 아니다");
    }

    [Fact]
    public void OnDeadlock_IgnoresReusedInfo_NoDuplicateEmit()   // DL-11 IsNew
    {
        var map = TestMaps.DeadEndCorridor();
        var collector = new EventCollector(map.MapId, "r", 100, TimeProvider.System);
        var lifecycle = new TaskLifecycle(map, new AStarPathPlanner(), new FleetOptions(), collector);
        collector.Begin(1, DateTimeOffset.UnixEpoch);

        var reused = new DeadlockInfo(new[] { "R-001", "R-002" }, new[] { new Point(1, 0), new Point(2, 0) },
            "failed", null, null, 7, 1, 0, DeadlockPhase.Failed, IsNew: false);
        lifecycle.OnDeadlock(reused, 5).Should().BeEmpty();

        collector.Complete(Array.Empty<RobotTelemetryPayload>())
            .SelectMany(b => b.Events).Should().NotContain(e => e.EventType == EventType.DeadlockDetected);
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
