using System.Text.Json;
using System.Text.Json.Serialization;
using RobotFleet.FleetController.Arbitration;
using RobotFleet.FleetController.Planning;
using RobotFleet.Shared.Domain;
using RobotFleet.Shared.Geometry;
using RobotFleet.Shared.Map;
using RobotFleet.Simulator;
using RobotFleet.TraceExport;
using TaskStatus = RobotFleet.Shared.Domain.TaskStatus;

// 실제 RobotStepper·A*·이동 판정으로 여러 대가 주차 → 픽업 ↔ 드롭오프 → 주차를 순환하게 돌리고,
// 전체 상태가 되풀이되는 한 주기를 JSON 트레이스로 남긴다. GIF 렌더러(tools/render)의 입력
const string Usage = "usage: --map <path> --out <trace.json> [--arbiter memory|always]";

var values = new Dictionary<string, string>();
for (var i = 0; i + 1 < args.Length; i += 2) values[args[i]] = args[i + 1];
if (args.Length % 2 != 0 || !values.TryGetValue("--map", out var mapPath) || !values.TryGetValue("--out", out var outPath))
{
    Console.Error.WriteLine(Usage);
    return 1;
}
var arbiterKind = values.GetValueOrDefault("--arbiter", "memory");
if (arbiterKind is not ("memory" or "always"))
{
    Console.Error.WriteLine(Usage);
    return 1;
}

var map = MapLoader.LoadFromFile(mapPath);
var places = new List<PlaceInfo>();
places.AddRange(map.Stations.Select(s => new PlaceInfo(s.Id, s.Type, s.X, s.Y, s.EquipmentId)));
places.AddRange(map.Chargers.Select(c => new PlaceInfo(c.Id, "charger", c.X, c.Y, null)));
places.AddRange(map.Parkings.Select(p => new PlaceInfo(p.Id, "parking", p.X, p.Y, null)));
var placeById = places.ToDictionary(p => p.Id);

var options = new SimulationOptions { MapPath = mapPath, Goal = default, SpeedFactor = 0, RenderEveryTicks = 0 };
var scenario = Scenarios.FactoryA;
var period = TicksOf(scenario.CycleSeconds);
var plans = scenario.Robots;
const int StuckTicks = 300;   // 이만큼 연속 대기하면 교착으로 본다

// 워밍업 뒤 전체 상태가 처음 되풀이되는 구간 [t0, t0 + 주기)를 찾는다. 앞쪽 한 주기는 궤적 잔상용으로 같이 남긴다
var warmup = plans.Max(StartTickOf) + 3L * period;
var run = await SimulateAsync(warmup + 5L * period);
if (run.Stuck is { } stuck)
{
    Console.Error.WriteLine(stuck);
    return 4;
}
if (FindLoop(run.Frames, (int)warmup) is not { } loop)
{
    Console.Error.WriteLine("system state does not repeat");
    return 4;
}
var (t0, loopTicks) = loop;
if (TraceChecks.FindPeriodBreak(run.Frames, t0 - loopTicks, loopTicks) is { } broken)
{
    Console.Error.WriteLine($"state at tick {broken} does not repeat after {loopTicks} ticks");
    return 4;
}
var frames = run.Frames.Skip(t0 - loopTicks).Take(2 * loopTicks + 1).ToList();

var robots = plans.Select(p => new RobotInfo(p.RobotId, p.Loop.Select(s => s.PlaceId).ToList())).ToList();
var trace = new Trace(
    map.MapId, map.Width, map.Height, GridRows(map), places, options.TickMs, options.MoveMsPerCell, options.TurnMsPer90,
    robots, loopTicks, loopTicks, frames);

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
await using (var stream = File.Create(outPath))
{
    await JsonSerializer.SerializeAsync(stream, trace, new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    });
}

var window = frames.Skip(loopTicks).ToList();
var conflicts = TraceChecks.FindConflicts(robots, window);
foreach (var c in conflicts.Take(20)) Console.Error.WriteLine(c);
if (conflicts.Count > 0)
{
    Console.Error.WriteLine($"{conflicts.Count} cell conflicts. change the scenario (trace written for inspection)");
    return 4;
}

foreach (var w in TraceChecks.WaitEpisodes(robots, window.Take(loopTicks).ToList()))
    Console.WriteLine($"  wait: {w}");
Console.WriteLine($"{outPath}: loop {loopTicks} ticks ({loopTicks * options.TickMs / 1000.0:F1}s) from tick {t0}, {plans.Count} robots, arbiter {arbiterKind}, no conflicts");
return 0;

int TicksOf(double seconds) => (int)Math.Round(seconds * 1000 / options.TickMs);

long StartTickOf(RobotPlan plan) => TicksOf(plan.StartDelaySeconds);

// 모든 로봇 상태(위치·방향·동작·진행률·작업 단계·대기 칸)가 같은 두 틱을 찾는다
static (int Start, int Length)? FindLoop(IReadOnlyList<TickFrame> all, int from)
{
    var seen = new Dictionary<string, int>();
    for (var t = from; t < all.Count; t++)
    {
        var key = string.Join("|", all[t].Robots);
        if (seen.TryGetValue(key, out var first))
            return first + 2 * (t - first) < all.Count ? (first, t - first) : null;
        seen[key] = t;
    }
    return null;
}

async Task<(List<TickFrame> Frames, string? Stuck)> SimulateAsync(long lastTick)
{
    IMoveArbiter arbiter = arbiterKind == "memory" ? new InMemoryMoveArbiter() : new AlwaysGrantMoveArbiter();
    var stepper = new RobotStepper(map, new AStarPathPlanner(), arbiter, options, new SimulationStats(), renderer: null);
    var agents = plans.Select(plan =>
    {
        var start = placeById[plan.Loop[0].PlaceId];
        var robot = new Robot { RobotId = plan.RobotId, MapId = map.MapId, Position = new Point(start.X, start.Y), Heading = plan.StartHeading };
        (arbiter as InMemoryMoveArbiter)?.Register(map.MapId, robot.RobotId, robot.Position);
        return new Agent(plan, robot, StartTickOf(plan));
    }).ToList();
    var taskSeq = 0;
    var result = new List<TickFrame> { Snapshot(0) };

    for (long tick = 1; tick <= lastTick; tick++)
    {
        foreach (var a in agents)
        {
            switch (a.Phase)
            {
                case Phase.Pending when tick > a.StartTick:
                    AssignNext(a);
                    break;
                case Phase.Dwelling when --a.DwellLeft <= 0:
                    a.Robot.Status = RobotStatus.Idle;
                    a.At = null;
                    AssignNext(a);
                    break;
            }
        }

        // 이동 요청은 StepAllAsync가 틱당 DecideAsync 1회로 모아 판정한다
        var running = agents.Where(a => a.Phase == Phase.Running).ToList();
        await stepper.StepAllAsync(running.Select(a => a.Robot).ToList(), tick);

        foreach (var a in running)
        {
            if (a.Task!.Status == TaskStatus.Failed)
                throw new InvalidOperationException($"tick {tick}: {a.Plan.RobotId} has no path to {a.Plan.Loop[a.Next].PlaceId}");
            a.Waited = a.Robot.Status == RobotStatus.Waiting ? a.Waited + 1 : 0;
            if (a.Waited >= StuckTicks)
                return (result, $"tick {tick}: {a.Plan.RobotId} waiting {a.Waited} ticks at {a.Robot.Position} (deadlock?)");
            if (a.Task.Status != TaskStatus.Completed) continue;

            var place = placeById[a.Plan.Loop[a.Next].PlaceId];
            a.Robot.Status = place.Kind switch
            {
                "pickup" => RobotStatus.Loading,
                "dropoff" => RobotStatus.Unloading,
                "charger" => RobotStatus.Charging,
                _ => RobotStatus.Idle,
            };
            a.At = place.Id;
            a.Phase = Phase.Dwelling;
            var dwell = TicksOf(a.Plan.Loop[a.Next].MinDwellSeconds);
            if (a.Next == 0)
            {
                // 주차 칸에서는 정해진 출발 시각(시작 시각 + 주기의 배수)까지 쉰다. 중간 대기가 생겨도 주기가 유지된다
                var depart = a.StartTick + ((tick + dwell - a.StartTick + period - 1) / period) * period;
                dwell = (int)(depart - tick);
            }
            a.DwellTotal = a.DwellLeft = dwell;
            a.Next = (a.Next + 1) % a.Plan.Loop.Count;
        }
        result.Add(Snapshot(tick));
    }
    return (result, null);

    void AssignNext(Agent a)
    {
        var place = placeById[a.Plan.Loop[a.Next].PlaceId];
        a.Task = new TransportTask
        {
            TaskId = $"T-{++taskSeq:D5}",
            MapId = map.MapId,
            Origin = a.Robot.Position,
            Destination = new Point(place.X, place.Y),
            CreatedAt = DateTimeOffset.UnixEpoch,
        };
        stepper.Assign(a.Robot, a.Task);
        a.Phase = Phase.Running;
    }

    TickFrame Snapshot(long tick) => new(tick, agents.Select(a =>
    {
        var r = a.Robot;
        var goal = a.Phase == Phase.Running ? a.Plan.Loop[a.Next].PlaceId : null;
        double? work = a.Phase == Phase.Dwelling ? Math.Round(1 - (double)a.DwellLeft / a.DwellTotal, 6) : null;
        Point? wait = r.Status == RobotStatus.Waiting && r.PathIndex + 1 < r.PlannedPath.Count ? r.PlannedPath[r.PathIndex + 1].Cell : null;
        return stepper.CurrentAction(r) is { } act
            ? new RobotFrame(r.Position.X, r.Position.Y, (int)r.Heading, r.Status.ToString(), act.Kind.ToString(),
                act.Kind == RobotActionKind.Move ? act.TargetCell.X : null,
                act.Kind == RobotActionKind.Move ? act.TargetCell.Y : null,
                act.Kind == RobotActionKind.Turn ? (int)act.TargetHeading : null,
                Math.Round(Progress(r, act), 3), goal, a.At, work, wait?.X, wait?.Y)
            : new RobotFrame(r.Position.X, r.Position.Y, (int)r.Heading, r.Status.ToString(), null, null, null, null, null,
                goal, a.At, work, wait?.X, wait?.Y);
    }).ToList());
}

double Progress(Robot r, RobotAction act)
{
    var total = act.Kind == RobotActionKind.Move
        ? options.MoveMsPerCell
        : r.Heading.TurnSteps(act.TargetHeading) * options.TurnMsPer90;
    return total <= 0 ? 1 : Math.Clamp(1 - (double)act.RemainingMs / total, 0, 1);
}

static List<string> GridRows(GridMap map) =>
    Enumerable.Range(0, map.Height)
        .Select(y => string.Concat(Enumerable.Range(0, map.Width).Select(x => (char)('0' + (int)map[x, y]))))
        .ToList();

internal enum Phase { Pending, Running, Dwelling }

internal sealed class Agent(RobotPlan plan, Robot robot, long startTick)
{
    public RobotPlan Plan { get; } = plan;
    public Robot Robot { get; } = robot;
    public long StartTick { get; } = startTick;
    public Phase Phase { get; set; } = Phase.Pending;
    public TransportTask? Task { get; set; }
    public int Next { get; set; } = 1;
    public string? At { get; set; }
    public int DwellLeft { get; set; }
    public int DwellTotal { get; set; }
    public int Waited { get; set; }
}
