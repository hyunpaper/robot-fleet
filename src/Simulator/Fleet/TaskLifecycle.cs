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
    private readonly EventCollector? _events;
    private readonly Dictionary<string, Run> _runs = new(StringComparer.Ordinal);

    public TaskLifecycle(GridMap map, IPathPlanner planner, FleetOptions options, EventCollector? events)
    {
        _map = map;
        _planner = planner;
        _options = options;
        _events = events;
    }

    public RobotStepper? Stepper { get; set; }

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
    }

    private sealed record Episode(long StartTick, Point Cell, string Type, string BlockedBy);
}
