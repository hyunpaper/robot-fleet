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

public class SimulationLoopTests
{
    private sealed record Sim(SimulationLoop Loop, Robot Robot, TransportTask Task)
    {
        public async Task StepAsync(int times)
        {
            for (var i = 0; i < times; i++)
                (await Loop.StepOnceAsync()).Should().BeTrue($"tick {Loop.Stats.Tick + 1} should not end the loop");
        }
    }

    private static Sim Create(GridMap map, Point start, Heading heading, Point goal,
        Func<SimulationOptions, SimulationOptions>? configure = null, IMoveArbiter? arbiter = null,
        ConsoleRenderer? renderer = null, ITickDelay? delay = null)
    {
        var options = new SimulationOptions { MapPath = "test", Start = start, StartHeading = heading, Goal = goal, SpeedFactor = 0 };
        if (configure is not null) options = configure(options);
        var robot = new Robot { RobotId = "R-001", MapId = map.MapId, Position = start, Heading = heading };
        var task = new TransportTask { TaskId = "T-00001", MapId = map.MapId, Type = TaskType.Park, Origin = start, Destination = goal };
        var loop = new SimulationLoop(map, robot, task, new AStarPathPlanner(), arbiter ?? new AlwaysGrantMoveArbiter(),
            options, delay ?? new NoDelay(), renderer);
        return new Sim(loop, robot, task);
    }

    [Fact] // T-01
    public async Task Straight3_Takes31Ticks()
    {
        var sim = Create(TestMaps.Open(5, 5), new(0, 0), Heading.East, new(3, 0));

        await sim.StepAsync(30);
        sim.Robot.Position.Should().Be(new Point(3, 0));
        sim.Robot.Status.Should().Be(RobotStatus.Moving);

        (await sim.Loop.StepOnceAsync()).Should().BeFalse();
        sim.Robot.Status.Should().Be(RobotStatus.Idle);
        sim.Task.Status.Should().Be(TaskStatus.Completed);
        sim.Loop.Stats.Tick.Should().Be(31);
        sim.Loop.Stats.TotalMoveMs.Should().Be(3000);
    }

    [Fact] // T-02
    public async Task InitialTurn_Takes34Ticks()
    {
        var sim = Create(TestMaps.Open(5, 5), new(0, 0), Heading.North, new(3, 0));

        await sim.StepAsync(2);
        sim.Robot.Heading.Should().Be(Heading.North);
        await sim.StepAsync(1);                                   // tick 3
        sim.Robot.Heading.Should().Be(Heading.East);
        await sim.StepAsync(9);                                   // tick 12
        sim.Robot.Position.Should().Be(new Point(0, 0));
        await sim.StepAsync(1);                                   // tick 13
        sim.Robot.Position.Should().Be(new Point(1, 0));
        await sim.StepAsync(20);                                  // tick 33
        (await sim.Loop.StepOnceAsync()).Should().BeFalse();
        sim.Loop.Stats.Tick.Should().Be(34);
        sim.Loop.Stats.TotalTurnMs.Should().Be(300);
    }

    [Fact] // T-03
    public async Task Turn180_Takes600ms()
    {
        var sim = Create(TestMaps.Open(5, 5), new(2, 0), Heading.West, new(4, 0));

        await sim.StepAsync(5);
        sim.Robot.Heading.Should().Be(Heading.West);
        await sim.StepAsync(1);                                   // tick 6
        sim.Robot.Heading.Should().Be(Heading.East);
        await sim.StepAsync(20);                                  // tick 26
        (await sim.Loop.StepOnceAsync()).Should().BeFalse();
        sim.Loop.Stats.Tick.Should().Be(27);
    }

    [Fact] // T-04
    public async Task MidPathTurn()
    {
        var sim = Create(TestMaps.Open(5, 5), new(0, 0), Heading.East, new(1, 1));

        await sim.StepAsync(1);
        sim.Robot.PlannedPath.Should().Equal(
            new PathStep(new(0, 0), Heading.East), new PathStep(new(1, 0), Heading.East), new PathStep(new(1, 1), Heading.South));
        await sim.StepAsync(9);                                   // tick 10
        sim.Robot.Position.Should().Be(new Point(1, 0));
        await sim.StepAsync(1);                                   // tick 11: Turn 시작
        sim.Loop.CurrentActionOf(sim.Robot)!.Kind.Should().Be(RobotActionKind.Turn);
        await sim.StepAsync(2);                                   // tick 13: Turn 완료
        sim.Robot.Heading.Should().Be(Heading.South);
        sim.Loop.CurrentActionOf(sim.Robot).Should().BeNull();
        await sim.StepAsync(1);                                   // tick 14: Move 시작
        sim.Loop.CurrentActionOf(sim.Robot)!.Kind.Should().Be(RobotActionKind.Move);
        await sim.StepAsync(8);                                   // tick 22
        sim.Robot.Position.Should().Be(new Point(1, 0));
        await sim.StepAsync(1);                                   // tick 23
        sim.Robot.Position.Should().Be(new Point(1, 1));
        (await sim.Loop.StepOnceAsync()).Should().BeFalse();
        sim.Loop.Stats.Tick.Should().Be(24);
    }

    [Fact] // T-05 — 1000 − 7×150 = −50 이월, 두 번째 Move는 950ms
    public async Task Carry_IsAppliedToNextAction()
    {
        var sim = Create(TestMaps.Open(5, 5), new(0, 0), Heading.East, new(2, 0), o => o with { TickMs = 150 });

        await sim.StepAsync(6);
        sim.Robot.Position.Should().Be(new Point(0, 0));
        await sim.StepAsync(1);                                   // tick 7
        sim.Robot.Position.Should().Be(new Point(1, 0));
        await sim.StepAsync(1);                                   // tick 8: 950으로 시작해 150 진행
        sim.Loop.CurrentActionOf(sim.Robot)!.RemainingMs.Should().Be(950 - 150);
        await sim.StepAsync(5);                                   // tick 13
        sim.Robot.Position.Should().Be(new Point(1, 0));
        await sim.StepAsync(1);                                   // tick 14
        sim.Robot.Position.Should().Be(new Point(2, 0));
        (await sim.Loop.StepOnceAsync()).Should().BeFalse();
        sim.Loop.Stats.Tick.Should().Be(15);
    }

    private static GridMap Island() => TestMaps.Unchecked("11011", "11011", "11011");

    [Fact] // T-06
    public async Task NoPath_ErrorsAndExits2()
    {
        var sim = Create(Island(), new(0, 0), Heading.East, new(4, 0));
        (await sim.Loop.StepOnceAsync()).Should().BeFalse();
        sim.Robot.Status.Should().Be(RobotStatus.Error);
        sim.Task.Status.Should().Be(TaskStatus.Failed);

        (await Create(Island(), new(0, 0), Heading.East, new(4, 0)).Loop.RunAsync()).Should().Be(2);
    }

    [Fact] // T-07
    public async Task AlreadyAtGoal_ArrivesOnFirstTick()
    {
        var sim = Create(TestMaps.Open(5, 5), new(2, 2), Heading.East, new(2, 2));
        (await sim.Loop.StepOnceAsync()).Should().BeFalse();
        sim.Robot.Status.Should().Be(RobotStatus.Idle);
        sim.Task.Status.Should().Be(TaskStatus.Completed);

        (await Create(TestMaps.Open(5, 5), new(2, 2), Heading.East, new(2, 2)).Loop.RunAsync()).Should().Be(0);
    }

    [Fact] // T-08
    public async Task MaxTicks_Exits3()
    {
        var sim = Create(TestMaps.Open(5, 5), new(0, 0), Heading.East, new(4, 4), o => o with { MaxTicks = 5 });

        (await sim.Loop.RunAsync()).Should().Be(3);
        sim.Loop.Stats.Tick.Should().Be(5);
        sim.Robot.Status.Should().Be(RobotStatus.Moving);
        (await sim.Loop.StepOnceAsync()).Should().BeFalse("종료 후에는 진행하지 않는다");
        sim.Loop.Stats.Tick.Should().Be(5);
    }

    [Fact] // T-09
    public async Task Arbiter_CalledOncePerMove_ReleasesPreviousCell()
    {
        var arbiter = new RecordingArbiter(grant: true);
        var sim = Create(TestMaps.Open(5, 5), new(0, 0), Heading.East, new(3, 0), arbiter: arbiter);

        (await sim.Loop.RunAsync()).Should().Be(0);
        arbiter.Decisions.Select(r => r.Tick).Should().Equal(1, 11, 21);
        arbiter.Decisions.Select(r => r.To).Should().Equal(new Point(1, 0), new Point(2, 0), new Point(3, 0));
        arbiter.Released.Should().Equal(new Point(0, 0), new Point(1, 0), new Point(2, 0));
    }

    [Fact] // T-09 보강 — 회전 틱에는 DecideAsync를 부르지 않는다
    public async Task Arbiter_NotCalledForTurns()
    {
        var arbiter = new RecordingArbiter(grant: true);
        var sim = Create(TestMaps.Open(5, 5), new(0, 0), Heading.North, new(1, 0), arbiter: arbiter);

        (await sim.Loop.RunAsync()).Should().Be(0);
        arbiter.Decisions.Should().ContainSingle().Which.Tick.Should().Be(4);
    }

    [Fact] // T-10 — Waiting은 매 틱 재요청하고, 대기 시간을 우선순위 입력으로 싣는다
    public async Task Denied_RobotWaits_RetryingEveryTick_UntilMaxTicks()
    {
        var arbiter = new RecordingArbiter(grant: false);
        var sim = Create(TestMaps.Open(5, 5), new(0, 0), Heading.East, new(3, 0), o => o with { MaxTicks = 20 }, arbiter);

        await sim.StepAsync(1);
        sim.Robot.Status.Should().Be(RobotStatus.Waiting);
        sim.Robot.Position.Should().Be(new Point(0, 0));

        (await sim.Loop.RunAsync()).Should().Be(3);
        sim.Robot.Status.Should().Be(RobotStatus.Waiting);
        sim.Robot.Position.Should().Be(new Point(0, 0));
        arbiter.Decisions.Select(r => r.Tick).Should().Equal(Enumerable.Range(1, 20).Select(t => (long)t));
        arbiter.Decisions.Select(r => r.WaitMs).Should().Equal(Enumerable.Range(0, 20).Select(i => i * 100));
    }

    [Fact] // T-10 보강 — 길이 열리면 Waiting → Moving, 대기 시간은 0으로
    public async Task Waiting_ResumesWhenGranted()
    {
        var arbiter = new RecordingArbiter(grant: false);
        var writer = new StringWriter();
        var map = TestMaps.Open(5, 5);
        var sim = Create(map, new(0, 0), Heading.East, new(2, 0), arbiter: arbiter, renderer: new ConsoleRenderer(writer, map, 100, 0));

        await sim.StepAsync(3);
        arbiter.Grant = true;
        await sim.StepAsync(1);                                   // tick 4: 승인
        sim.Robot.Status.Should().Be(RobotStatus.Moving);
        await sim.StepAsync(9);                                   // tick 13
        sim.Robot.Position.Should().Be(new Point(1, 0));
        await sim.StepAsync(1);                                   // tick 14: 다음 칸
        arbiter.Decisions.Select(r => r.WaitMs).Should().Equal(0, 100, 200, 300, 0);

        var lines = writer.ToString().TrimEnd().Split(Environment.NewLine);
        lines.Where(l => l.Contains(" WAIT ", StringComparison.Ordinal)).Should().Equal(
            "[t=000001 0.1s] R-001 WAIT    (0,0)   reason=Occupied");
        lines.Should().Contain("[t=000004 0.4s] R-001 MOVE    (0,0) → (1,0)");
    }

    [Fact] // T-11 — 전체 시나리오의 이벤트 로그
    public async Task EventLog_ForStraight3()
    {
        var writer = new StringWriter();
        var map = TestMaps.Open(5, 5);
        var sim = Create(map, new(0, 0), Heading.East, new(3, 0), renderer: new ConsoleRenderer(writer, map, 100, 0));

        (await sim.Loop.RunAsync()).Should().Be(0);

        var lines = writer.ToString().TrimEnd().Split(Environment.NewLine);
        lines[0].Should().StartWith("[t=000001 0.1s] R-001 PLAN    (0,0)E → (3,0)   steps=3 cost=3.0 ");
        lines[^1].Should().Be("[t=000031 3.1s] R-001 ARRIVED (3,0)   ticks=31 moves=3 turns=0 overrun=0");
        lines.Where(l => l.Contains(" MOVE ", StringComparison.Ordinal)).Should().Equal(
            "[t=000001 0.1s] R-001 MOVE    (0,0) → (1,0)",
            "[t=000011 1.1s] R-001 MOVE    (1,0) → (2,0)",
            "[t=000021 2.1s] R-001 MOVE    (2,0) → (3,0)");
    }

    [Fact] // T-12 — 예산 1ms를 느린 arbiter가 넘긴다
    public async Task Overrun_IsCounted()
    {
        var delay = new RecordingDelay();
        var sim = Create(TestMaps.Open(5, 5), new(0, 0), Heading.East, new(3, 0),
            o => o with { SpeedFactor = 1, TickMs = 1 }, new RecordingArbiter(grant: true, sleepMs: 5), delay: delay);

        (await sim.Loop.RunAsync()).Should().Be(0);
        sim.Loop.Stats.OverrunCount.Should().BeGreaterThan(0);
        (sim.Loop.Stats.OverrunCount + delay.Calls).Should().Be((int)sim.Loop.Stats.Tick - 1, "도착 틱을 뺀 모든 틱은 대기하거나 초과로 센다");
    }

    [Fact]
    public async Task Canceled_Exits130()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var sim = Create(TestMaps.Open(5, 5), new(0, 0), Heading.East, new(3, 0));

        (await sim.Loop.RunAsync(cts.Token)).Should().Be(130);
    }

    [Fact]
    public async Task CanceledDuringDelay_Exits130()
    {
        using var cts = new CancellationTokenSource();
        var sim = Create(TestMaps.Open(5, 5), new(0, 0), Heading.East, new(3, 0),
            o => o with { SpeedFactor = 1, TickMs = 50 }, delay: new CancelingDelay(cts));

        (await sim.Loop.RunAsync(cts.Token)).Should().Be(130);
        sim.Loop.Stats.Tick.Should().Be(1);
    }

    [Fact] // T-13 — 기본 시나리오 통합
    public async Task FactoryA_DefaultScenario()
    {
        var map = MapLoader.LoadFromFile(Path.Combine(AppContext.BaseDirectory, "maps", "factory-a.json"));
        var writer = new StringWriter();
        var sim = Create(map, new(0, 39), Heading.North, new(6, 3), renderer: new ConsoleRenderer(writer, map, 100, 0));

        (await sim.Loop.RunAsync()).Should().Be(0);
        sim.Loop.Stats.Tick.Should().Be(427);
        sim.Robot.Position.Should().Be(new Point(6, 3));
        sim.Robot.Heading.Should().Be(Heading.North);

        var lines = writer.ToString().TrimEnd().Split(Environment.NewLine);
        lines.Should().Contain("[t=000341 34.1s] R-001 TURN    N → E")
            .And.Contain("[t=000344 34.4s] R-001 MOVE    (0,5) → (1,5)");
        lines[^1].Should().Be("[t=000427 42.7s] R-001 ARRIVED (6,3)   ticks=427 moves=42 turns=2 overrun=0");
    }

    [Fact] // T-13 — 메모리 점유 판정으로도 단일 로봇 결과는 같다
    public async Task FactoryA_DefaultScenario_WithInMemoryArbiter()
    {
        var map = MapLoader.LoadFromFile(Path.Combine(AppContext.BaseDirectory, "maps", "factory-a.json"));
        var arbiter = new InMemoryMoveArbiter();
        arbiter.Register(map.MapId, "R-001", new(0, 39));
        var sim = Create(map, new(0, 39), Heading.North, new(6, 3), arbiter: arbiter);

        (await sim.Loop.RunAsync()).Should().Be(0);
        sim.Loop.Stats.Tick.Should().Be(427);
        arbiter.OccupantOf(map.MapId, new(6, 3)).Should().Be("R-001");
        arbiter.OccupantOf(map.MapId, new(0, 39)).Should().BeNull();
    }

    private sealed class RecordingArbiter(bool grant, int sleepMs = 0) : IMoveArbiter
    {
        public bool Grant { get; set; } = grant;

        public List<MoveRequest> Decisions { get; } = new();
        public List<Point> Released { get; } = new();

        public ValueTask<IReadOnlyList<MoveDecision>> DecideAsync(
            string mapId, long tick, IReadOnlyList<MoveRequest> requests, CancellationToken ct = default)
        {
            if (sleepMs > 0) Thread.Sleep(sleepMs);
            Decisions.AddRange(requests);
            IReadOnlyList<MoveDecision> result = requests
                .Select(r => Grant ? MoveDecision.Grant(r.RobotId) : new MoveDecision(r.RobotId, false, MoveDenyReason.Occupied))
                .ToArray();
            return ValueTask.FromResult(result);
        }

        public ValueTask ReleaseAsync(string mapId, string robotId, Point cell, CancellationToken ct = default)
        {
            Released.Add(cell);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingDelay : ITickDelay
    {
        public int Calls { get; private set; }

        public Task DelayAsync(int ms, CancellationToken ct)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }

    private sealed class CancelingDelay(CancellationTokenSource cts) : ITickDelay
    {
        public Task DelayAsync(int ms, CancellationToken ct)
        {
            cts.Cancel();
            return Task.FromCanceled(ct);
        }
    }
}
