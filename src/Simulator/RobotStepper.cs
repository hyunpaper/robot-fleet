using RobotFleet.FleetController.Arbitration;
using RobotFleet.FleetController.Planning;
using RobotFleet.Shared.Domain;
using RobotFleet.Shared.Geometry;
using RobotFleet.Shared.Map;
using TaskStatus = RobotFleet.Shared.Domain.TaskStatus;

namespace RobotFleet.Simulator;

/// <summary>
/// 로봇의 틱 진행(계획 → 시작 → 진행). 06 "틱 처리 순서"를 그대로 따른다.
/// M2에서 루프가 요청을 모아 DecideAsync를 1회 부르도록 BuildMoveRequest / ApplyAsync로 나눠 둔다.
/// </summary>
public sealed class RobotStepper
{
    private readonly GridMap _map;
    private readonly IPathPlanner _planner;
    private readonly IMoveArbiter _arbiter;
    private readonly SimulationOptions _options;
    private readonly SimulationStats _stats;
    private readonly ConsoleRenderer? _renderer;
    private readonly Dictionary<string, State> _states = new();

    public RobotStepper(GridMap map, IPathPlanner planner, IMoveArbiter arbiter, SimulationOptions options,
                        SimulationStats stats, ConsoleRenderer? renderer)
    {
        _map = map;
        _planner = planner;
        _arbiter = arbiter;
        _options = options;
        _stats = stats;
        _renderer = renderer;
    }

    /// <summary>로봇에 작업을 배정한다. 다음 틱의 1단계가 경로를 계획한다.</summary>
    public void Assign(Robot robot, TransportTask task)
    {
        robot.CurrentTaskId = task.TaskId;
        task.AssignedRobotId = robot.RobotId;
        task.Status = TaskStatus.Assigned;
        StateOf(robot).Task = task;
    }

    /// <summary>진행 중인 동작. 없으면 null.</summary>
    public RobotAction? CurrentAction(Robot robot) => StateOf(robot).Action;

    /// <summary>한 틱 전체. M1은 로봇 1대라 결정을 직접 받아 적용한다.</summary>
    public async ValueTask StepAsync(Robot robot, long tick, CancellationToken ct = default)
    {
        var request = BuildMoveRequest(robot, tick);
        MoveDecision? decision = null;
        if (request is { } r)
            decision = (await _arbiter.DecideAsync(robot.MapId, tick, new[] { r }, ct))[0];
        await ApplyAsync(robot, tick, decision, ct);
    }

    /// <summary>1단계(계획)와 2단계의 회전·도착 분기까지. 이동이 필요하면 요청을 돌려주고, 아니면 null.</summary>
    public MoveRequest? BuildMoveRequest(Robot robot, long tick)
    {
        var state = StateOf(robot);
        var task = state.Task;

        // 1. [계획]
        if (robot.Status == RobotStatus.Idle && task is { Status: TaskStatus.Assigned } && robot.PlannedPath.Count == 0)
        {
            var result = _planner.FindPath(_map, robot.Position, robot.Heading, task.Destination);
            if (!result.IsSuccess)
            {
                robot.Status = RobotStatus.Error;
                task.Status = TaskStatus.Failed;
                _renderer?.LogNoPath(tick, robot.RobotId, robot.Position, task.Destination, result.Status);
                return null;
            }
            robot.PlannedPath = result.Steps;
            robot.PathIndex = 0;
            robot.Status = RobotStatus.Moving;
            task.Status = TaskStatus.InProgress;
            _renderer?.LogPlan(tick, robot.RobotId, robot.Position, robot.Heading, task.Destination, result);
        }

        // 2. [시작] — 회전·도착 분기
        if (state.Action is not null || robot.Status != RobotStatus.Moving)
            return null;

        if (robot.PathIndex == robot.PlannedPath.Count - 1)
        {
            Arrive(robot, state, tick);
            return null;
        }

        var next = robot.PlannedPath[robot.PathIndex + 1];
        if (robot.Heading != next.Heading)
        {
            var turnMs = robot.Heading.TurnSteps(next.Heading) * _options.TurnMsPer90;
            state.Action = new RobotAction { Kind = RobotActionKind.Turn, TargetHeading = next.Heading, RemainingMs = turnMs + state.Carry };
            state.ActionMs = turnMs;
            state.Carry = 0;
            _renderer?.LogTurn(tick, robot.RobotId, robot.Heading, next.Heading);
            return null;
        }

        return new MoveRequest(
            robot.RobotId, robot.Position, next.Cell, next.Heading, tick,
            RemainingSteps: robot.PlannedPath.Count - 1 - robot.PathIndex,
            TaskPriority: task?.Priority ?? 0);
    }

    /// <summary>2단계의 결정 적용(decision이 있을 때)과 3단계 진행.</summary>
    public async ValueTask ApplyAsync(Robot robot, long tick, MoveDecision? decision, CancellationToken ct = default)
    {
        var state = StateOf(robot);

        if (decision is { } d)
        {
            var next = robot.PlannedPath[robot.PathIndex + 1];
            if (!d.Granted)
            {
                robot.Status = RobotStatus.Waiting;   // M1에선 발생하지 않음. 재시도는 M2
                _renderer?.LogWait(tick, robot.RobotId, robot.Position, d.Reason);
                return;
            }
            state.Action = new RobotAction { Kind = RobotActionKind.Move, TargetCell = next.Cell, RemainingMs = _options.MoveMsPerCell + state.Carry };
            state.ActionMs = _options.MoveMsPerCell;
            state.Carry = 0;
            _renderer?.LogMove(tick, robot.RobotId, robot.Position, next.Cell);
        }

        // 3. [진행] — 이 틱에 시작한 동작 포함. 완료돼도 새 동작은 다음 틱에
        var action = state.Action;
        if (action is null) return;

        action.RemainingMs -= _options.TickMs;
        if (action.RemainingMs > 0) return;

        if (action.Kind == RobotActionKind.Turn)
        {
            robot.Heading = action.TargetHeading;
            _stats.TotalTurnMs += state.ActionMs;
            state.Turns++;
        }
        else
        {
            await _arbiter.ReleaseAsync(robot.MapId, robot.RobotId, robot.Position, ct);   // 이전 칸
            robot.Position = action.TargetCell;
            robot.PathIndex++;
            _stats.TotalMoveMs += state.ActionMs;
            state.Moves++;
        }
        state.Carry = action.RemainingMs;
        state.Action = null;
    }

    private void Arrive(Robot robot, State state, long tick)
    {
        robot.Status = RobotStatus.Idle;
        robot.PlannedPath = Array.Empty<PathStep>();
        robot.PathIndex = 0;
        robot.CurrentTaskId = null;
        if (state.Task is { } task) task.Status = TaskStatus.Completed;
        state.Carry = 0;
        _renderer?.LogArrived(tick, robot.RobotId, robot.Position, tick, state.Moves, state.Turns, _stats.OverrunCount);
        state.Moves = state.Turns = 0;
    }

    private State StateOf(Robot robot)
    {
        if (!_states.TryGetValue(robot.RobotId, out var state))
            _states[robot.RobotId] = state = new State();
        return state;
    }

    // Robot(Shared)에 두지 않는 시뮬레이터 전용 진행 상태
    private sealed class State
    {
        public TransportTask? Task;
        public RobotAction? Action;
        public int ActionMs;     // 이월 제외 명목 시간. 통계용
        public int Carry;        // 직전 동작의 잔여(0 또는 음수). 다음 동작에 더한다
        public int Moves, Turns;
    }
}
