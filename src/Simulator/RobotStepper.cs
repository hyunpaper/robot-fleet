using RobotFleet.FleetController.Arbitration;
using RobotFleet.FleetController.Planning;
using RobotFleet.Shared.Domain;
using RobotFleet.Shared.Geometry;
using RobotFleet.Shared.Map;
using TaskStatus = RobotFleet.Shared.Domain.TaskStatus;

namespace RobotFleet.Simulator;

/// <summary>
/// 로봇의 틱 진행(계획 → 시작 → 진행). 06 "틱 처리 순서"를 그대로 따른다.
/// StepAllAsync가 여러 로봇의 요청을 모아 DecideAsync를 1회 부르고, 그 앞뒤가 BuildMoveRequest / ApplyAsync다.
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
    private readonly IRobotObserver? _observer;

    /// observer: 상태 전이 · 거부를 관찰한다(M3 이벤트 수집). 동작에는 영향이 없다
    public RobotStepper(GridMap map, IPathPlanner planner, IMoveArbiter arbiter, SimulationOptions options,
                        SimulationStats stats, ConsoleRenderer? renderer, IRobotObserver? observer = null)
    {
        _observer = observer;
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

    /// <summary>로봇 1대의 한 틱 전체.</summary>
    public ValueTask StepAsync(Robot robot, long tick, CancellationToken ct = default)
        => StepAllAsync(new[] { robot }, tick, ct);

    /// <summary>여러 로봇의 한 틱. 요청을 모두 모아 맵마다 DecideAsync 1회 → 각 로봇에 적용(01 틱 루프).</summary>
    public async ValueTask StepAllAsync(IReadOnlyList<Robot> robots, long tick, CancellationToken ct = default)
    {
        var decisions = new MoveDecision?[robots.Count];
        var pending = new List<(int Index, MoveRequest Request)>();
        for (var i = 0; i < robots.Count; i++)
        {
            if (BuildMoveRequest(robots[i], tick) is { } r)
                pending.Add((i, r));
        }

        foreach (var group in pending.GroupBy(p => robots[p.Index].MapId))
        {
            var batch = group.ToArray();
            var result = await _arbiter.DecideAsync(group.Key, tick, batch.Select(p => p.Request).ToArray(), ct);
            for (var k = 0; k < batch.Length; k++)
                decisions[batch[k].Index] = result[k];
        }

        // 교착 해소를 관찰자에 넘긴다(13 §2). 양보로 경로를 새로 받은 로봇·실패로 Error가 된 로봇은 이번 틱 결정을 건너뛴다
        var skip = new HashSet<string>(StringComparer.Ordinal);
        if (_observer is not null)
            foreach (var decision in decisions)
                if (decision?.Deadlock is { } info)
                    foreach (var r in _observer.OnDeadlock(info, tick))
                        skip.Add(r);

        for (var i = 0; i < robots.Count; i++)
            await ApplyAsync(robots[i], tick, skip.Contains(robots[i].RobotId) ? null : decisions[i], ct);
    }

    /// 교착 양보: 로봇에 명시 경로(회피 지점 경유)를 통째로 심는다. 상태는 Moving으로, 관찰자 통지는 호출자(생애주기)가 한다. 13 §5
    public void SetPath(Robot robot, IReadOnlyList<PathStep> steps)
    {
        var state = StateOf(robot);
        robot.PlannedPath = steps;
        robot.PathIndex = 0;
        state.Action = null;
        state.WaitMs = 0;
        robot.Status = RobotStatus.Moving;
    }

    public enum ReplanOutcome { Applied, NoAlternative, Failed }

    /// 재계획(13 §6). 현재 위치에서 goal로 다시 계획해 남은 경로와 다르면 적용(대안), 같으면 NoAlternative(대기 유지), 경로 없으면 Failed.
    /// 적용 시 WaitMs를 0으로(§6c). 상태 통지는 호출자가 한다
    public (ReplanOutcome Outcome, int OldLength, int NewLength, double ComputeMs) Replan(Robot robot, Point goal)
    {
        var state = StateOf(robot);
        var result = _planner.FindPath(_map, robot.Position, robot.Heading, goal);
        if (!result.IsSuccess)
            return (ReplanOutcome.Failed, 0, 0, result.Elapsed.TotalMilliseconds);

        var remaining = robot.PlannedPath.Skip(robot.PathIndex).Select(s => s.Cell).ToList();
        if (result.Steps.Select(s => s.Cell).SequenceEqual(remaining))
            return (ReplanOutcome.NoAlternative, 0, 0, result.Elapsed.TotalMilliseconds);

        var oldLength = Math.Max(0, remaining.Count - 1);
        SetPath(robot, result.Steps);
        return (ReplanOutcome.Applied, oldLength, result.MoveCount, result.Elapsed.TotalMilliseconds);
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
                SetStatus(robot, RobotStatus.Error, tick);
                task.Status = TaskStatus.Failed;
                _renderer?.LogNoPath(tick, robot.RobotId, robot.Position, task.Destination, result.Status);
                return null;
            }
            robot.PlannedPath = result.Steps;
            robot.PathIndex = 0;
            SetStatus(robot, RobotStatus.Moving, tick);
            task.Status = TaskStatus.InProgress;
            _renderer?.LogPlan(tick, robot.RobotId, robot.Position, robot.Heading, task.Destination, result);
        }

        // 2. [시작] — 회전·도착 분기. Waiting은 이미 다음 칸을 향해 있으므로 바로 재요청
        if (state.Action is not null || robot.Status is not (RobotStatus.Moving or RobotStatus.Waiting))
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
            WaitMs: state.WaitMs,
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
                // 이 틱은 제자리. 다음 틱에 대기 시간을 실어 재요청한다
                if (robot.Status != RobotStatus.Waiting)
                    _renderer?.LogWait(tick, robot.RobotId, robot.Position, d.Reason);
                _observer?.OnDenied(robot, d, next.Cell, tick);
                SetStatus(robot, RobotStatus.Waiting, tick);
                state.WaitMs += _options.TickMs;
                state.Carry = 0;
                return;
            }
            SetStatus(robot, RobotStatus.Moving, tick);   // Waiting이었다면 path_cleared
            state.WaitMs = 0;
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

    private void SetStatus(Robot robot, RobotStatus status, long tick)
    {
        var from = robot.Status;
        robot.Status = status;
        if (from != status) _observer?.OnStatusChanged(robot, from, status, tick);
    }

    private void Arrive(Robot robot, State state, long tick)
    {
        SetStatus(robot, RobotStatus.Idle, tick);
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
        public int WaitMs;       // 연속 거부 누적. 우선순위 입력, 승인되면 0
        public int Moves, Turns;
    }
}
