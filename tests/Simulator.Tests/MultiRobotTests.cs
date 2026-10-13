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
        => await RunDeadlock(map, maxTicks, new DeadlockOptions(), specs);

    private static async Task<(IReadOnlyList<TransportTask> Tasks, IReadOnlyList<Robot> Robots, List<EventItem> Events)> RunDeadlock(
        GridMap map, int maxTicks, DeadlockOptions deadlockOptions, params (string Id, Point Start, Heading Heading, Point Goal)[] specs)
    {
        var options = new FleetOptions { WorkMs = 0, TickMs = 100, SpeedFactor = 0, Robots = specs.Length };
        var planner = new AStarPathPlanner();
        var collector = new EventCollector(map.MapId, "r1", options.TickMs, TimeProvider.System);
        var lifecycle = new TaskLifecycle(map, planner, options, collector);
        var arbiter = new InMemoryMoveArbiter(deadlockOptions, options.TickMs);
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
        long last = 0;
        for (long tick = 1; tick <= maxTicks; tick++)
        {
            last = tick;
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

        // run 종료 드레인(FleetSimulation 종료 경로와 동일): 미해소 교착을 failed로 발행
        collector.Begin(last + 1, DateTimeOffset.UnixEpoch.AddMilliseconds((last + 1) * options.TickMs));
        lifecycle.FlushDeadlocks(arbiter.DrainPendingDeadlocks(map.MapId, last));
        foreach (var b in collector.Complete(Array.Empty<RobotTelemetryPayload>())) events.AddRange(b.Events);

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
    public async Task DL08_HeadOn_DeadEndCorridor_NoFailure_RetriesThenDrainFailed()
    {
        // 회피 지점이 없는 1칸 통로 — 작업을 실패시키지 않고 대기 유지 + 재시도. 종료 드레인에서만 failed 1건(사용자 결정)
        var (tasks, robots, events) = await RunDeadlock(TestMaps.DeadEndCorridor(), 80,
            ("R-001", new(1, 0), Heading.East, new(3, 0)),
            ("R-002", new(2, 0), Heading.West, new(0, 0)));

        tasks.Should().OnlyContain(t => t.Status != TaskStatus.Failed, "작업을 실패시키지 않는다");
        robots.Should().OnlyContain(r => r.Status != RobotStatus.Error, "로봇을 Error로 만들지 않는다");
        robots.Should().Contain(r => r.Status == RobotStatus.Waiting, "대기를 유지한다");
        events.Should().NotContain(e => e.EventType == EventType.TaskFailed
                                        && StoreMapping.Read<TaskFailedPayload>(e.Payload).Reason == "deadlock_unresolved");

        // 에피소드당 1건, 종료 드레인에서 failed (noYieldRetries > 0 = 회피 지점 없이 재시도했다)
        var deadlocks = Deadlocks(events);
        deadlocks.Should().ContainSingle();
        deadlocks[0].Resolution.Should().Be("failed");
        deadlocks[0].NoYieldRetries.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task DL09_ThreeVersusOne_NoPingPongAndFiniteTermination()
    {
        // 동쪽 1대(R-001) 대 서쪽 3대 행렬. 통로 중앙 포켓 (4,1)로 R-001이 양보하면 서쪽 3대가 지나가 서쪽에 주차하고,
        // R-001은 다시 동쪽 끝으로 간다 — 완료 로봇이 R-001의 복귀 경로를 막지 않도록 목적지를 배치했다
        var map = TestMaps.Unchecked("1111111111", "0000100000");   // 포켓 (4,1)
        var (tasks, robots, events) = await RunDeadlock(map, 600,
            ("R-001", new(5, 0), Heading.East, new(9, 0)),
            ("R-002", new(6, 0), Heading.West, new(0, 0)),
            ("R-003", new(7, 0), Heading.West, new(1, 0)),
            ("R-004", new(8, 0), Heading.West, new(2, 0)));

        var deadlocks = Deadlocks(events);
        deadlocks.Should().NotBeEmpty("해소가 일어나야 한다");
        // 같은 로봇의 양보는 재계획 상한(replan_max_per_task=5)에 묶여 유한하다 — 무한 핑퐁 없음
        deadlocks.Where(d => d.Resolution == "yield")
            .GroupBy(d => d.YieldRobotId).Should().OnlyContain(g => g.Count() <= new FleetOptions().ReplanMaxPerTask);
        // 유한 틱 종료: 모든 작업이 끝 상태, 작업을 든 채 멈춘 로봇 없음
        tasks.Should().OnlyContain(t => t.Status == TaskStatus.Completed || t.Status == TaskStatus.Failed, "모든 작업이 끝 상태");
        robots.Should().OnlyContain(r => r.Status != RobotStatus.Waiting, "작업을 든 채 대기하는 로봇이 없다");
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
    public async Task DL_TtlExpiry_NoEmitDuringRun_RetriesAndResolves()
    {
        // 포켓이 거리 3, TTL 30(04 범위 안)이라 양보가 회전 지연으로 TTL을 넘길 수 있다. TTL 만료는 발행 없이 재시도로 전환 —
        // 에피소드가 열린 채 다음 시도로 이어지고, 결국 양보로 해소되거나(yield) 대안으로 빠진다(reroute). 중복 failed 발행 없음
        var opts = new DeadlockOptions { YieldSearchMaxDistance = 3, DeadlockResolveTtlTicks = 30 };
        var (tasks, _, events) = await RunDeadlock(TestMaps.Unchecked("1111111", "0001000"), 500, opts,
            ("R-001", new(2, 0), Heading.East, new(6, 0)),
            ("R-002", new(3, 0), Heading.West, new(0, 0)));

        events.Should().NotContain(e => e.EventType == EventType.TaskFailed
            && StoreMapping.Read<TaskFailedPayload>(e.Payload).Reason == "deadlock_unresolved", "작업을 실패시키지 않는다");
        // 에피소드당 1건만 — failed+yield 두 건으로 남기지 않는다
        var deadlocks = Deadlocks(events);
        deadlocks.GroupBy(d => d.Cycle[0]).Should().OnlyContain(g => g.Count() == 1, "에피소드당 1건");
        tasks.Should().OnlyContain(t => t.Status == TaskStatus.Completed, "결국 해소되어 둘 다 완료");
    }

    [Fact]
    public void OnDeadlock_IgnoresReusedInfo_NoDuplicateEmit()   // DL-11 IsNew
    {
        var map = TestMaps.DeadEndCorridor();
        var collector = new EventCollector(map.MapId, "r", 100, TimeProvider.System);
        var lifecycle = new TaskLifecycle(map, new AStarPathPlanner(), new FleetOptions(), collector);
        collector.Begin(1, DateTimeOffset.UnixEpoch);

        var reused = new DeadlockInfo(new[] { "R-001", "R-002" }, new[] { new Point(1, 0), new Point(2, 0) },
            "failed", null, null, 7, 1, 0, DeadlockPhase.Failed, IsNew: false, NoYieldRetries: 1);
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
