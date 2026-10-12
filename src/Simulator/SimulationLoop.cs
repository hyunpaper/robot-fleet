using System.Diagnostics;
using RobotFleet.FleetController.Arbitration;
using RobotFleet.FleetController.Planning;
using RobotFleet.Shared.Domain;
using RobotFleet.Shared.Map;
using TaskStatus = RobotFleet.Shared.Domain.TaskStatus;

namespace RobotFleet.Simulator;

/// <summary>틱 루프. 종료 조건은 도착 / 경로 없음(Error) / MaxTicks / 취소.</summary>
public sealed class SimulationLoop
{
    public const int ExitArrived = 0;
    public const int ExitFailed = 2;
    public const int ExitMaxTicks = 3;
    public const int ExitCanceled = 130;

    private readonly Robot _robot;
    private readonly TransportTask _task;
    private readonly SimulationOptions _options;
    private readonly ITickDelay _delay;
    private readonly ConsoleRenderer? _renderer;
    private readonly RobotStepper _stepper;
    private bool _finished;

    public SimulationStats Stats { get; } = new();

    public SimulationLoop(GridMap map, Robot robot, TransportTask task, IPathPlanner planner,
                          IMoveArbiter arbiter, SimulationOptions options, ITickDelay delay, ConsoleRenderer? renderer)
    {
        _robot = robot;
        _task = task;
        _options = options;
        _delay = delay;
        _renderer = renderer;
        _stepper = new RobotStepper(map, planner, arbiter, options, Stats, renderer);
        _stepper.Assign(robot, task);
    }

    /// <summary>로봇의 진행 중인 동작. 테스트 관찰용.</summary>
    public RobotAction? CurrentActionOf(Robot robot) => _stepper.CurrentAction(robot);

    /// <summary>한 틱만 진행. 테스트가 직접 호출한다. false = 종료 조건 도달(이후 호출은 아무것도 안 함).</summary>
    public async ValueTask<bool> StepOnceAsync(CancellationToken ct = default)
    {
        if (_finished) return false;

        Stats.Tick++;
        await _stepper.StepAsync(_robot, Stats.Tick, ct);
        _renderer?.OnTick(Stats.Tick, _robot);

        _finished = _task.Status is TaskStatus.Completed or TaskStatus.Failed || Stats.Tick >= _options.MaxTicks;
        return !_finished;
    }

    /// <summary>종료 조건까지 실행. 반환값 = 종료 코드 (0 도착, 2 경로 없음/Error, 3 MaxTicks 초과, 130 취소).</summary>
    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        var wall = Stopwatch.StartNew();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var sw = Stopwatch.StartNew();
                if (!await StepOnceAsync(ct)) break;

                if (_options.SpeedFactor > 0)
                {
                    var budget = _options.TickMs / _options.SpeedFactor;
                    var wait = budget - sw.Elapsed.TotalMilliseconds;
                    if (wait > 0) await _delay.DelayAsync((int)wait, ct);
                    else Stats.OverrunCount++;         // 01: 틱 초과는 M1부터 센다
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 대기 중 취소. 아래에서 130
        }
        finally
        {
            Stats.WallClock = wall.Elapsed;
        }
        return ExitCode(ct);
    }

    private int ExitCode(CancellationToken ct) => _task.Status switch
    {
        TaskStatus.Completed => ExitArrived,
        TaskStatus.Failed => ExitFailed,
        _ when ct.IsCancellationRequested => ExitCanceled,
        _ => ExitMaxTicks,
    };
}
