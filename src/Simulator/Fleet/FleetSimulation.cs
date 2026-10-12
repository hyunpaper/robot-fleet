using System.Diagnostics;
using RobotFleet.FleetController.Arbitration;
using RobotFleet.FleetController.Planning;
using RobotFleet.Shared.Domain;
using RobotFleet.Shared.Geometry;
using RobotFleet.Shared.Map;
using RobotFleet.Shared.Messaging;
using RobotFleet.Shared.Pipeline;
using RobotFleet.Simulator.Events;

namespace RobotFleet.Simulator.Fleet;

/// 로봇 N대 루프: 생성 → 체류 → 할당 → StepAllAsync → 이벤트 확정 → 송신 버퍼. 10 §1 · §5
public sealed class FleetSimulation
{
    public const int ExitOk = 0;
    public const int ExitSinkClosed = 4;
    public const int ExitCanceled = 130;

    private readonly GridMap _map;
    private readonly FleetOptions _options;
    private readonly IEventSink? _sink;
    private readonly ITickDelay _delay;
    private readonly ConsoleRenderer? _renderer;
    private readonly TimeProvider _time;
    private readonly List<Robot> _robots = [];
    private readonly TaskGenerator _generator;
    private readonly TaskDispatcher _dispatcher = new();
    private readonly TaskLifecycle _lifecycle;
    private readonly RobotStepper _stepper;
    private readonly EventCollector? _events;
    private long _tick;
    private long _overruns;
    private TimeSpan _stall;

    /// sink가 null이면 이벤트를 만들지 않는다
    public FleetSimulation(GridMap map, FleetOptions options, IEventSink? sink, ITickDelay delay, ConsoleRenderer? renderer)
        : this(map, options, sink, delay, renderer, TimeProvider.System)
    {
    }

    public FleetSimulation(GridMap map, FleetOptions options, IEventSink? sink, ITickDelay delay, ConsoleRenderer? renderer,
                           TimeProvider time)
    {
        _map = map;
        _options = options;
        _sink = sink;
        _delay = delay;
        _renderer = renderer;
        _time = time;
        _generator = new TaskGenerator(map, options.Lambda, options.TickMs, options.Seed);
        _events = sink is null ? null : new EventCollector(map.MapId, options.TickMs, time);
        var planner = new AStarPathPlanner();
        _lifecycle = new TaskLifecycle(map, planner, options, _events);

        var arbiter = new InMemoryMoveArbiter();
        foreach (var (cell, i) in StartCells(map, options.Robots).Select((c, i) => (c, i)))
        {
            var robot = new Robot { RobotId = $"R-{i + 1:D3}", MapId = map.MapId, Position = cell, Heading = Heading.North };
            arbiter.Register(map.MapId, robot.RobotId, cell);
            _robots.Add(robot);
            _lifecycle.Add(robot);
        }

        var stepOptions = new SimulationOptions
        {
            MapPath = "",
            Goal = default,
            TickMs = options.TickMs,
            MoveMsPerCell = options.MoveMsPerCell,
            TurnMsPer90 = options.TurnMsPer90,
            SpeedFactor = options.SpeedFactor,
        };
        _stepper = new RobotStepper(map, planner, arbiter, stepOptions, new SimulationStats(), renderer: null, observer: _lifecycle);
        _lifecycle.Stepper = _stepper;
    }

    public IReadOnlyList<Robot> Robots => _robots;

    public long Tick => _tick;

    public async Task<FleetRunResult> RunAsync(CancellationToken ct)
    {
        var exit = ExitOk;
        try
        {
            while (_tick < _options.MaxTicks && !ct.IsCancellationRequested)
            {
                var sw = Stopwatch.StartNew();
                if (!await StepOnceAsync(ct))
                {
                    exit = ExitSinkClosed;
                    break;
                }

                if (_options.SpeedFactor > 0)
                {
                    var wait = _options.TickMs / _options.SpeedFactor - sw.Elapsed.TotalMilliseconds;
                    if (wait > 0) await _delay.DelayAsync((int)wait, ct);
                    else Overrun();
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            exit = ExitCanceled;
        }

        if (exit != ExitSinkClosed && _events is not null)
        {
            // 열린 대기 구간을 닫아 마지막 배치로 보낸다(종료 시 ConflictDetected 유실 방지)
            _events.Begin(_tick, _time.GetUtcNow());
            _lifecycle.FlushOpenConflicts(_tick);
            foreach (var batch in _events.Complete([]).Where(b => b.Events.Count > 0))
            {
                if (!await WriteAsync(batch, CancellationToken.None)) exit = ExitSinkClosed;
            }
        }

        return new FleetRunResult(_tick, _generator.Created, _lifecycle.Completed, _lifecycle.Failed, _overruns, _stall, exit);
    }

    /// 한 틱. false = 송신 버퍼가 닫혔다(400 등) → 시뮬레이션 중단
    public async Task<bool> StepOnceAsync(CancellationToken ct)
    {
        _tick++;
        var now = _time.GetUtcNow();
        _events?.Begin(_tick, now);

        foreach (var task in _generator.Next(now))
        {
            _events?.TaskCreated(task);
            _dispatcher.Enqueue(task);
        }

        _lifecycle.AdvanceDwell(_tick);
        foreach (var (task, robot) in _dispatcher.Dispatch(_lifecycle.IdleRobots()))
            _lifecycle.Assign(robot, task, _tick);

        await _stepper.StepAllAsync(_robots, _tick, ct);
        _lifecycle.AfterStep();

        if (_events is null) return true;
        foreach (var batch in _events.Complete(_robots.Select(_lifecycle.TelemetryOf)))
        {
            SimulatorMetrics.CountGenerated(batch);
            if (!await WriteAsync(batch, ct)) return false;
        }

        return true;
    }

    // 틱 루프는 대기하지 않는다. 버퍼가 가득 찼을 때만 틱 정지(10 §5)
    private async Task<bool> WriteAsync(EventBatch batch, CancellationToken ct)
    {
        if (_sink!.TryWrite(batch)) return true;
        var stalled = Stopwatch.StartNew();
        try
        {
            while (!_sink.TryWrite(batch))
            {
                if (!await _sink.WaitToWriteAsync(ct)) return false;
            }

            return true;
        }
        finally
        {
            _stall += stalled.Elapsed;
            SimulatorMetrics.TickStall.WithLabels("event_buffer").Observe(stalled.Elapsed.TotalSeconds);
        }
    }

    private void Overrun()
    {
        _overruns++;
        SimulatorMetrics.TickOverrun.WithLabels(_map.MapId).Inc();
    }

    // Parking 칸을 먼저, 모자라면 통행 가능한 칸을 행 순서로
    private static IEnumerable<Point> StartCells(GridMap map, int count)
    {
        var cells = map.Parkings.Select(p => new Point(p.X, p.Y)).ToList();
        for (var y = 0; y < map.Height && cells.Count < count; y++)
        {
            for (var x = 0; x < map.Width && cells.Count < count; x++)
            {
                var p = new Point(x, y);
                if (map.IsWalkable(p) && !cells.Contains(p) && map.Stations.All(s => s.X != x || s.Y != y))
                    cells.Add(p);
            }
        }

        if (cells.Count < count)
            throw new ArgumentException($"{map.MapId}에 로봇 {count}대를 둘 칸이 없다");
        return cells.Take(count);
    }
}
