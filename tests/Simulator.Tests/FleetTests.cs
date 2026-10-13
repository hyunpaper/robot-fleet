using FluentAssertions;
using RobotFleet.FleetController.Arbitration;
using RobotFleet.FleetController.Planning;
using RobotFleet.Shared.Domain;
using RobotFleet.Shared.Geometry;
using RobotFleet.Shared.Map;
using RobotFleet.Shared.Messaging;
using RobotFleet.Shared.Pipeline;
using RobotFleet.Simulator.Events;
using RobotFleet.Simulator.Fleet;
using Xunit;

namespace RobotFleet.Simulator.Tests;

/// G-01~G-06 · E-01~E-04 (10 §12). 인프라 없는 순수 로직 — 이벤트는 CaptureEvents로 받는다
public sealed class FleetTests
{
    private static readonly GridMap Factory = MapLoader.LoadFromFile(Path.Combine(AppContext.BaseDirectory, "maps", "factory-a.json"));

    private static FleetOptions Options(int robots, double lambda, long ticks, int seed = 1) => new()
    {
        Robots = robots,
        Lambda = lambda,
        Seed = seed,
        MaxTicks = ticks,
        SpeedFactor = 0,
    };

    private static async Task<(FleetRunResult Result, List<EventBatch> Batches)> RunAsync(FleetOptions options)
    {
        var batches = new List<EventBatch>();
        var sim = new FleetSimulation(Factory, options, sink: null, new NoDelay(), null);
        sim.CaptureEvents(batches.Add);
        var result = await sim.RunAsync(default);
        return (result, batches);
    }

    private static IEnumerable<(EventBatch Batch, EventItem Item)> Items(IEnumerable<EventBatch> batches) =>
        batches.SelectMany(b => b.Events.Select(e => (b, e)));

    [Fact]
    public void G01_같은_시드는_같은_작업열_출발과_목적은_다르다()
    {
        var a = new TaskGenerator(Factory, 5, 100, 42);
        var b = new TaskGenerator(Factory, 5, 100, 42);
        var at = DateTimeOffset.UnixEpoch;
        var ta = Enumerable.Range(0, 200).SelectMany(_ => a.Next(at)).ToList();
        var tb = Enumerable.Range(0, 200).SelectMany(_ => b.Next(at)).ToList();
        ta.Select(t => (t.TaskId, t.Origin, t.Destination)).Should().Equal(tb.Select(t => (t.TaskId, t.Origin, t.Destination)));
        ta.Should().NotBeEmpty().And.OnlyContain(t => t.Origin != t.Destination);
    }

    [Fact]
    public void G02_포아송_평균은_람다의_3퍼센트_안()
    {
        var g = new TaskGenerator(Factory, 5, 100, 7);
        var total = Enumerable.Range(0, 100_000).Sum(_ => g.Next(DateTimeOffset.UnixEpoch).Count);
        (total / 100_000.0).Should().BeApproximately(0.5, 0.5 * 0.03);
    }

    [Fact]
    public void G03_최근접_Idle_동점은_robotId_순()
    {
        var d = new TaskDispatcher();
        d.Enqueue(new TransportTask { TaskId = "T-1", MapId = "m", Origin = new Point(5, 5), Destination = new Point(9, 9) });
        d.Enqueue(new TransportTask { TaskId = "T-2", MapId = "m", Origin = new Point(0, 0), Destination = new Point(9, 9) });
        Robot R(string id, int x, int y) => new() { RobotId = id, MapId = "m", Position = new Point(x, y) };
        var pairs = d.Dispatch([R("R-3", 5, 6), R("R-2", 4, 5), R("R-1", 0, 1)]);
        pairs.Select(p => (p.Task.TaskId, p.Robot.RobotId)).Should().Equal(("T-1", "R-2"), ("T-2", "R-1"));
    }

    [Fact]
    public async Task G04_생애주기_전이와_사유_순서()
    {
        var (result, batches) = await RunAsync(Options(1, 0.5, 3000));
        result.TasksCompleted.Should().BeGreaterThan(0);
        var reasons = Items(batches).Where(x => x.Item.EventType == EventType.RobotStatusChanged)
            .Select(x => StoreMapping.Read<RobotStatusChangedPayload>(x.Item.Payload).Reason)
            .Take(5).ToList();
        reasons.Should().Equal("task_assigned", "arrived_origin", "load_done", "arrived_destination", "unload_done");

        var taskEvents = Items(batches).Select(x => x.Item.EventType).Where(t => t.StartsWith("Task", StringComparison.Ordinal)).ToList();
        taskEvents.Should().ContainInOrder(EventType.TaskCreated, EventType.TaskAssigned, EventType.TaskStarted, EventType.TaskCompleted);
    }

    [Fact]
    public async Task G05_출발지에서_할당되면_같은_틱에_Idle_Moving_Loading()
    {
        var events = await SingleTaskAsync(origin: StationCell(0), destination: StationCell(1), robotAt: StationCell(0));
        events.Select(e => e.EventType).Should().ContainInOrder(EventType.RobotStatusChanged, EventType.RobotStatusChanged, EventType.TaskStarted);
        var statuses = events.Where(e => e.EventType == EventType.RobotStatusChanged)
            .Select(e => StoreMapping.Read<RobotStatusChangedPayload>(e.Payload)).ToList();
        statuses.Select(s => (s.From, s.To, s.Reason)).Should().Equal((0, 1, "task_assigned"), (1, 3, "arrived_origin"));
    }

    [Fact]
    public async Task G06_경로_없음은_Moving_Error와_TaskFailed()
    {
        var blocked = Enumerable.Range(0, Factory.Height).SelectMany(y => Enumerable.Range(0, Factory.Width).Select(x => new Point(x, y)))
            .First(p => !Factory.IsWalkable(p));
        var events = await SingleTaskAsync(origin: blocked, destination: StationCell(1), robotAt: StationCell(0));
        var statuses = events.Where(e => e.EventType == EventType.RobotStatusChanged)
            .Select(e => StoreMapping.Read<RobotStatusChangedPayload>(e.Payload)).ToList();
        statuses.Select(s => (s.From, s.To, s.Reason)).Should().Equal((0, 1, "task_assigned"), (1, 6, "no_path"));
        events.Should().Contain(e => e.EventType == EventType.TaskFailed);
    }

    [Fact]
    public void E01_배치_안_순서()
    {
        var c = new EventCollector("m", "r1", 100, TimeProvider.System);
        c.Begin(1, DateTimeOffset.UnixEpoch);
        c.TaskCreated(new TransportTask { TaskId = "T-1", MapId = "m", Origin = new Point(1, 1), Destination = new Point(2, 2) });
        c.Status("R-2", RobotStatus.Idle, RobotStatus.Moving, "task_assigned");
        c.TaskEvent("R-1", EventType.TaskStarted, new TaskStartedPayload("T-9", "R-1"));
        c.Status("R-1", RobotStatus.Moving, RobotStatus.Loading, "arrived_origin");
        c.Conflict("R-1", new ConflictDetectedPayload("vertex", 1, 1, ["R-1", "R-3"], "R-3", "wait", 100));
        var batch = c.Complete([Tel("R-2"), Tel("R-1")]).Single();

        batch.Events.Select(e => (e.EventType, Key: RoutingKeys.Of(e.EventType, e.Payload, "m"))).Should().Equal(
            (EventType.TaskCreated, "T-1"),
            (EventType.ConflictDetected, "m"),
            (EventType.RobotStatusChanged, "R-1"),
            (EventType.TaskStarted, "T-9"),
            (EventType.RobotStatusChanged, "R-2"),
            (EventType.RobotTelemetry, "R-1"),
            (EventType.RobotTelemetry, "R-2"));
    }

    [Fact]
    public async Task E02_대기_구간마다_ConflictDetected_1건_waitMs는_구간_길이()
    {
        var (_, batches) = await RunAsync(Options(30, 3, 3000));
        var items = Items(batches).ToList();
        var waits = items.Count(x => x.Item.EventType == EventType.RobotStatusChanged
                                     && StoreMapping.Read<RobotStatusChangedPayload>(x.Item.Payload).Reason == "conflict_wait");
        var conflicts = items.Where(x => x.Item.EventType == EventType.ConflictDetected)
            .Select(x => StoreMapping.Read<ConflictDetectedPayload>(x.Item.Payload)).ToList();
        waits.Should().BeGreaterThan(0);
        conflicts.Should().HaveCount(waits);
        conflicts.Should().OnlyContain(c => c.WaitMs >= 100 && c.WaitMs % 100 == 0);
    }

    [Fact]
    public void E03_1000건을_넘으면_순서를_지키며_자른다()
    {
        var c = new EventCollector("m", "r1", 100, TimeProvider.System, maxPerBatch: 3);
        c.Begin(1, DateTimeOffset.UnixEpoch);
        var batches = c.Complete(Enumerable.Range(0, 7).Select(i => Tel($"R-{i}")));
        batches.Select(b => b.Events.Count).Should().Equal(3, 3, 1);
        batches.SelectMany(b => b.Events).Select(e => StoreMapping.Read<RobotTelemetryPayload>(e.Payload).RobotId)
            .Should().Equal(Enumerable.Range(0, 7).Select(i => $"R-{i}"));
        batches.Select(b => b.BatchId).Should().BeInAscendingOrder(StringComparer.Ordinal);
        batches.Should().OnlyContain(b => b.Tick == 1);
    }

    [Fact]
    public async Task E04_모든_상태_전이가_03_표_안()
    {
        var (result, batches) = await RunAsync(Options(30, 3, 3000, seed: 3));   // 표 밖이면 수집기가 예외
        result.TasksCompleted.Should().BeGreaterThan(10);
        Items(batches).Where(x => x.Item.EventType == EventType.RobotStatusChanged)
            .Select(x => StoreMapping.Read<RobotStatusChangedPayload>(x.Item.Payload))
            .Should().OnlyContain(s => StatusTransitions.IsAllowed((RobotStatus)s.From, (RobotStatus)s.To, s.Reason));
        batches.Should().OnlyContain(b => b.TickMs == 100 && b.Events.Count <= FleetSchema.MaxEventsPerBatch);
    }

    private static Point StationCell(int i) => new(Factory.Stations[i].X, Factory.Stations[i].Y);

    private static RobotTelemetryPayload Tel(string robot) => new(robot, 0, 0, 0, 0, 100, null, 0, 0);

    // 로봇 1대 · 작업 1건을 직접 조립해 첫 틱의 이벤트를 본다
    private static async Task<IReadOnlyList<EventItem>> SingleTaskAsync(Point origin, Point destination, Point robotAt)
    {
        var options = Options(1, 0, 10);
        var collector = new EventCollector(Factory.MapId, options.RunId, options.TickMs, TimeProvider.System);
        var planner = new AStarPathPlanner();
        var lifecycle = new TaskLifecycle(Factory, planner, options, collector);
        var robot = new Robot { RobotId = "R-001", MapId = Factory.MapId, Position = robotAt, Heading = Heading.North };
        var arbiter = new InMemoryMoveArbiter();
        arbiter.Register(Factory.MapId, robot.RobotId, robotAt);
        lifecycle.Add(robot);
        var stepper = new RobotStepper(Factory, planner, arbiter,
            new SimulationOptions { MapPath = "", Goal = default }, new SimulationStats(), null, lifecycle);
        lifecycle.Stepper = stepper;

        collector.Begin(1, DateTimeOffset.UnixEpoch);
        lifecycle.Assign(robot, new TransportTask { TaskId = "T-1", MapId = Factory.MapId, Origin = origin, Destination = destination }, 1);
        await stepper.StepAllAsync([robot], 1);
        return collector.Complete([]).SelectMany(b => b.Events).ToList();
    }
}
