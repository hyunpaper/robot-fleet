using RobotFleet.FleetController.Arbitration;
using RobotFleet.FleetController.Planning;
using RobotFleet.Shared.Domain;
using RobotFleet.Shared.Geometry;
using RobotFleet.Shared.Map;
using RobotFleet.Shared.Messaging;
using RobotFleet.Simulator.Events;
using TaskStatus = RobotFleet.Shared.Domain.TaskStatus;

namespace RobotFleet.Simulator.Fleet;

public enum FleetPhase { Idle, ToOrigin, Loading, ToDestination, Unloading, Error }

/// 작업 1건 = 이동 구간 2개(→ 출발지, → 목적지) + 적재 · 하역 체류. RobotStepper에는 구간마다 내부 작업을 넘기고,
/// 관찰한 원시 전이를 03 전이 · 사유로 바꿔 이벤트를 낸다. 10 §1
internal sealed class TaskLifecycle : IRobotObserver
{
    private readonly GridMap _map;
    private readonly IPathPlanner _planner;
    private readonly FleetOptions _options;
    private EventCollector? _events;
    private readonly Dictionary<string, Run> _runs = new(StringComparer.Ordinal);

    public TaskLifecycle(GridMap map, IPathPlanner planner, FleetOptions options, EventCollector? events)
    {
        _map = map;
        _planner = planner;
        _options = options;
        _events = events;
    }

    public RobotStepper? Stepper { get; set; }

    public void UseEvents(EventCollector events) => _events = events;

    public int Completed { get; private set; }

    public int Failed { get; private set; }

    public void Add(Robot robot) => _runs[robot.RobotId] = new Run(robot);

    public FleetPhase PhaseOf(Robot robot) => _runs[robot.RobotId].Phase;

    public IEnumerable<Robot> IdleRobots() => _runs.Values.Where(r => r.Phase == FleetPhase.Idle).Select(r => r.Robot);

    public void Assign(Robot robot, TransportTask task, long tick)
    {
        var run = _runs[robot.RobotId];
        run.Task = task;
        run.Phase = FleetPhase.ToOrigin;
        run.Moves = run.MoveTicks = run.WaitTicks = 0;
        task.AssignedRobotId = robot.RobotId;
        task.Status = TaskStatus.Assigned;
        robot.CurrentTaskId = task.TaskId;

        var leg1 = _planner.FindPath(_map, robot.Position, robot.Heading, task.Origin);
        var startHeading = leg1.IsSuccess ? leg1.Steps[^1].Heading : robot.Heading;
        var leg2 = _planner.FindPath(_map, task.Origin, startHeading, task.Destination);
        var planned = leg1.MoveCount + leg2.MoveCount;
        _events?.TaskEvent(robot.RobotId, EventType.TaskAssigned, new TaskAssignedPayload(task.TaskId, robot.RobotId, planned,
            planned * _options.MoveMsPerCell + 2 * _options.WorkMs));
        StartLeg(run, task.Origin, tick);
    }

    /// 체류 진행. 적재가 끝나면 2구간을 시작하고, 하역이 끝나면 작업 완료
    public void AdvanceDwell(long tick)
    {
        foreach (var run in _runs.Values)
        {
            if (run.Phase is not (FleetPhase.Loading or FleetPhase.Unloading)) continue;
            run.DwellLeftMs -= _options.TickMs;
            if (run.DwellLeftMs > 0) continue;

            var robot = run.Robot;
            var task = run.Task!;
            if (run.Phase == FleetPhase.Loading)
            {
                robot.Status = RobotStatus.Idle;   // 내부용. 다음 계획이 Loading → Moving(load_done)으로 보고된다
                run.Phase = FleetPhase.ToDestination;
                StartLeg(run, task.Destination, tick);
                continue;
            }

            robot.Status = RobotStatus.Idle;
            Report(run, RobotStatus.Idle, "unload_done");
            task.Status = TaskStatus.Completed;
            robot.CurrentTaskId = null;
            _events?.TaskEvent(robot.RobotId, EventType.TaskCompleted, new TaskCompletedPayload(task.TaskId, robot.RobotId,
                run.Moves, run.MoveTicks * _options.TickMs, run.WaitTicks * _options.TickMs, 2 * _options.WorkMs, 0));
            run.Task = null;
            run.Phase = FleetPhase.Idle;
            Completed++;
        }
    }

    /// StepAllAsync 뒤: 위치 변화 · 상태 시간을 센다
    public void AfterStep()
    {
        foreach (var run in _runs.Values)
        {
            if (run.Robot.Position != run.LastPosition)
            {
                run.Moves++;
                run.Battery = Math.Max(0, run.Battery - 0.05);
                run.LastPosition = run.Robot.Position;
            }

            if (run.Phase is FleetPhase.ToOrigin or FleetPhase.ToDestination)
            {
                if (run.Robot.Status == RobotStatus.Moving) run.MoveTicks++;
                else if (run.Robot.Status == RobotStatus.Waiting) run.WaitTicks++;
            }
        }
    }

    public RobotTelemetryPayload TelemetryOf(Robot robot)
    {
        var run = _runs[robot.RobotId];
        return new RobotTelemetryPayload(robot.RobotId, robot.Position.X, robot.Position.Y, (int)robot.Heading, (int)robot.Status,
            (int)Math.Round(run.Battery), robot.CurrentTaskId, robot.PathIndex, Math.Max(0, robot.PlannedPath.Count - 1));
    }

    /// 시뮬레이션 종료: 열린 대기 구간을 ConflictDetected로 닫는다
    public void FlushOpenConflicts(long tick)
    {
        foreach (var run in _runs.Values.Where(r => r.Episode is not null))
            CloseEpisode(run, tick);
    }

    public void OnDenied(Robot robot, MoveDecision decision, Point target, long tick)
    {
        var run = _runs[robot.RobotId];
        run.Episode ??= new Episode(tick, target, decision.Reason == MoveDenyReason.Swap ? "edge" : "vertex",
            decision.BlockedBy ?? robot.RobotId);
    }

    /// 판정 응답의 교착 해소를 적용한다(13 §2 · §5). 반환 = 이번 틱 결정을 건너뛸 로봇들
    public IReadOnlyCollection<string> OnDeadlock(DeadlockInfo info, long tick)
    {
        if (!info.IsNew) return Array.Empty<string>();   // 재사용·재전송은 중복 발행·실행하지 않는다
        switch (info.Phase)
        {
            case DeadlockPhase.Detected:
                var yielded = StartYield(info, tick);
                return yielded is null ? Array.Empty<string>() : new[] { yielded };
            case DeadlockPhase.Resolved:
                EmitDeadlock(info);
                if (_runs.TryGetValue(info.YieldRobotId!, out var yr)) yr.Yielding = false;
                return Array.Empty<string>();
            case DeadlockPhase.Failed:
                EmitDeadlock(info);
                // TTL 만료(양보가 안 끝남) = YieldRobotId 있음: 이벤트만, 양보 로봇은 재계획으로 복귀(13 §5). 작업은 실패시키지 않는다.
                // 회피 지점 없음 = YieldRobotId null: 순환 전원 실패
                if (info.YieldRobotId is { } ttlYield)
                {
                    if (_runs.TryGetValue(ttlYield, out var tr)) tr.Yielding = false;
                    return Array.Empty<string>();
                }

                return FailCycle(info, tick);
            default:
                return Array.Empty<string>();
        }
    }

    // 양보: 회피 지점을 경유해 원래 목적지로 가는 경로를 통째로 심는다(13 §5). 회피 지점에서 멈추지 않고 waypoint로 지난다
    private string? StartYield(DeadlockInfo info, long tick)
    {
        if (info.YieldRobotId is not { } robotId || info.YieldTo is not { } yieldTo) return null;
        if (!_runs.TryGetValue(robotId, out var run) || run.Task is not { } task || Stepper is null) return null;

        var robot = run.Robot;
        var legGoal = run.Phase == FleetPhase.ToOrigin ? task.Origin : task.Destination;
        var leg1 = _planner.FindPath(_map, robot.Position, robot.Heading, yieldTo);
        if (!leg1.IsSuccess) return null;
        var leg2 = _planner.FindPath(_map, yieldTo, leg1.Steps[^1].Heading, legGoal);
        var combined = new List<PathStep>(leg1.Steps);
        if (leg2.IsSuccess) combined.AddRange(leg2.Steps.Skip(1));

        var oldLength = Math.Max(0, robot.PlannedPath.Count - 1 - robot.PathIndex);
        Stepper.SetPath(robot, combined);
        if (run.Reported == RobotStatus.Waiting)   // 대기 중이던 로봇만 상태 전이(Waiting→Moving yield). 이미 Moving이면 경로만 바꾼다
        {
            Report(run, RobotStatus.Moving, "yield");
            CloseEpisode(run, tick);
            run.WaitTicks = 0;
        }

        _events?.Replan(robotId, new PathReplannedPayload(robotId, task.TaskId, "deadlock", oldLength, combined.Count - 1,
            leg1.Elapsed.TotalMilliseconds));
        run.Yielding = true;
        run.ReplanCount++;   // deadlock 재계획도 상한에 센다(§7)
        return robotId;
    }

    // 회피 지점 없는 교착: 순환 전원 작업 실패 + Waiting → Error(13 §5)
    private IReadOnlyCollection<string> FailCycle(DeadlockInfo info, long tick)
    {
        var failed = new List<string>();
        foreach (var robotId in info.Cycle)
        {
            // 실제로 막혀 대기 중인 로봇만 실패시킨다(진짜 교착은 전원 Waiting). 양보 중·이동 중은 건드리지 않는다
            if (!_runs.TryGetValue(robotId, out var run) || run.Task is not { } task) continue;
            if (run.Yielding || run.Reported != RobotStatus.Waiting) continue;
            Report(run, RobotStatus.Error, "deadlock_unresolved");
            CloseEpisode(run, tick);
            run.Robot.Status = RobotStatus.Error;
            _events?.TaskEvent(robotId, EventType.TaskFailed,
                new TaskFailedPayload(task.TaskId, robotId, "deadlock_unresolved", run.Robot.Position.X, run.Robot.Position.Y));
            task.Status = TaskStatus.Failed;
            run.Robot.CurrentTaskId = null;
            run.Task = null;
            run.Phase = FleetPhase.Error;
            Failed++;
            failed.Add(robotId);
        }

        return failed;
    }

    private void EmitDeadlock(DeadlockInfo info)
    {
        var cells = info.Cells.Select(p => new CellDto(p.X, p.Y)).ToList();
        _events?.Deadlock(info.Cycle[0], new DeadlockDetectedPayload(info.Cycle, cells, info.Resolution,
            info.YieldRobotId ?? "", info.YieldTo?.X ?? 0, info.YieldTo?.Y ?? 0, info.SearchedDistance, info.ResolvedMs));
    }

    /// 틱 끝: 막혀 대기 중인 로봇의 재계획 트리거·쿨다운·상한(13 §6). FleetSimulation이 StepAllAsync·AfterStep 뒤에 부른다
    public void ApplyReplans(long tick)
    {
        if (Stepper is null) return;
        var cooldownTicks = Math.Max(1, _options.ReplanCooldownMs / _options.TickMs);
        foreach (var run in _runs.Values)
        {
            if (run.Yielding || run.Phase is FleetPhase.Idle or FleetPhase.Error) continue;
            if (run.Robot.Status != RobotStatus.Waiting || run.Task is not { } task) continue;
            if (run.WaitTicks * _options.TickMs <= _options.ReplanWaitThresholdMs) continue;
            if (run.LastReplanTick >= 0 && tick - run.LastReplanTick < cooldownTicks) continue;

            if (run.ReplanCount >= _options.ReplanMaxPerTask)
            {
                Report(run, RobotStatus.Error, "replan_exceeded");
                CloseEpisode(run, tick);
                run.Robot.Status = RobotStatus.Error;
                _events?.TaskEvent(run.Robot.RobotId, EventType.TaskFailed,
                    new TaskFailedPayload(task.TaskId, run.Robot.RobotId, "replan_exceeded", run.Robot.Position.X, run.Robot.Position.Y));
                task.Status = TaskStatus.Failed;
                run.Robot.CurrentTaskId = null;
                run.Task = null;
                run.Phase = FleetPhase.Error;
                Failed++;
                continue;
            }

            var goal = run.Phase == FleetPhase.ToOrigin ? task.Origin : task.Destination;
            var (outcome, oldLen, newLen, ms) = Stepper.Replan(run.Robot, goal);
            run.LastReplanTick = tick;
            if (outcome != RobotStepper.ReplanOutcome.Applied) continue;   // §6b 대안 없음·경로 없음 → 대기 유지(교착 탐지로)

            run.ReplanCount++;
            Report(run, RobotStatus.Moving, "replanned");
            CloseEpisode(run, tick);
            _events?.Replan(run.Robot.RobotId, new PathReplannedPayload(run.Robot.RobotId, task.TaskId, "blocked", oldLen, newLen, ms));
            run.WaitTicks = 0;
        }
    }

    /// run 종료: 드레인된 미완료 교착을 DeadlockDetected(failed)로 발행(13 §2 · §5)
    public void FlushDeadlocks(IReadOnlyList<DeadlockInfo> drained)
    {
        foreach (var info in drained)
            EmitDeadlock(info);
    }

    public void OnStatusChanged(Robot robot, RobotStatus from, RobotStatus to, long tick)
    {
        var run = _runs[robot.RobotId];
        switch (from, to)
        {
            case (RobotStatus.Idle, RobotStatus.Moving):
                Report(run, RobotStatus.Moving, StartReason(run));
                break;
            case (RobotStatus.Moving, RobotStatus.Waiting):
                Report(run, RobotStatus.Waiting, "conflict_wait");
                break;
            case (RobotStatus.Waiting, RobotStatus.Moving):
                Report(run, RobotStatus.Moving, "path_cleared");
                CloseEpisode(run, tick);
                run.WaitTicks = 0;   // 대기 종료 — 재계획 트리거(§6)가 묵은 대기로 오발하지 않게
                break;
            case (RobotStatus.Moving, RobotStatus.Idle):
                Arrived(run);
                break;
            case (_, RobotStatus.Error):
                if (run.Reported != RobotStatus.Moving)
                    Report(run, RobotStatus.Moving, StartReason(run));   // 03은 Idle · Loading → Error(no_path)가 없다
                Report(run, RobotStatus.Error, "no_path");
                CloseEpisode(run, tick);
                Fail(run);
                break;
            default:
                throw new InvalidOperationException($"{robot.RobotId}: 예상하지 못한 원시 전이 {from} → {to}");
        }
    }

    private void Arrived(Run run)
    {
        var robot = run.Robot;
        var task = run.Task!;
        if (run.Phase == FleetPhase.ToOrigin)
        {
            robot.Status = RobotStatus.Loading;
            Report(run, RobotStatus.Loading, "arrived_origin");
            task.Status = TaskStatus.InProgress;
            _events?.TaskEvent(robot.RobotId, EventType.TaskStarted, new TaskStartedPayload(task.TaskId, robot.RobotId));
            run.Phase = FleetPhase.Loading;
        }
        else
        {
            robot.Status = RobotStatus.Unloading;
            Report(run, RobotStatus.Unloading, "arrived_destination");
            run.Phase = FleetPhase.Unloading;
        }

        run.DwellLeftMs = _options.WorkMs;
    }

    private void Fail(Run run)
    {
        var robot = run.Robot;
        if (run.Task is { } task)
        {
            task.Status = TaskStatus.Failed;
            _events?.TaskEvent(robot.RobotId, EventType.TaskFailed,
                new TaskFailedPayload(task.TaskId, robot.RobotId, "no_path", robot.Position.X, robot.Position.Y));
        }

        robot.CurrentTaskId = null;
        run.Task = null;
        run.Phase = FleetPhase.Error;
        Failed++;
    }

    private void StartLeg(Run run, Point goal, long tick)
    {
        _ = tick;
        var leg = new TransportTask
        {
            TaskId = $"{run.Task!.TaskId}/{(run.Phase == FleetPhase.ToOrigin ? 1 : 2)}",
            MapId = run.Task.MapId,
            Type = TaskType.Transport,
            Origin = run.Robot.Position,
            Destination = goal,
            CreatedAt = run.Task.CreatedAt,
        };
        Stepper!.Assign(run.Robot, leg);
        run.Robot.CurrentTaskId = run.Task.TaskId;
    }

    private static string StartReason(Run run) => run.Reported == RobotStatus.Loading ? "load_done" : "task_assigned";

    private void Report(Run run, RobotStatus to, string reason)
    {
        _events?.Status(run.Robot.RobotId, run.Reported, to, reason);
        run.Reported = to;
    }

    private void CloseEpisode(Run run, long tick)
    {
        if (run.Episode is not { } e) return;
        run.Episode = null;
        _events?.Conflict(run.Robot.RobotId, new ConflictDetectedPayload(e.Type, e.Cell.X, e.Cell.Y,
            [run.Robot.RobotId, e.BlockedBy], e.BlockedBy, "wait", (int)(tick - e.StartTick) * _options.TickMs));
    }

    private sealed class Run(Robot robot)
    {
        public Robot Robot { get; } = robot;

        public FleetPhase Phase { get; set; } = FleetPhase.Idle;

        public RobotStatus Reported { get; set; } = robot.Status;

        public TransportTask? Task { get; set; }

        public int DwellLeftMs { get; set; }

        public int Moves { get; set; }

        public int MoveTicks { get; set; }

        public int WaitTicks { get; set; }

        public double Battery { get; set; } = robot.BatteryPercent;

        public Point LastPosition { get; set; } = robot.Position;

        public Episode? Episode { get; set; }

        public bool Yielding { get; set; }

        public int ReplanCount { get; set; }

        public long LastReplanTick { get; set; } = -1;
    }

    private sealed record Episode(long StartTick, Point Cell, string Type, string BlockedBy);
}
