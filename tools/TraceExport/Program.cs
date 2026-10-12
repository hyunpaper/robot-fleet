using System.Text.Json;
using RobotFleet.FleetController.Arbitration;
using RobotFleet.FleetController.Planning;
using RobotFleet.Shared.Domain;
using RobotFleet.Shared.Geometry;
using RobotFleet.Shared.Map;
using RobotFleet.Simulator;
using RobotFleet.TraceExport;
using TaskStatus = RobotFleet.Shared.Domain.TaskStatus;

// 실제 RobotStepper·A*로 여러 대를 돌려 틱별 상태를 JSON 트레이스로 남긴다. GIF 렌더러(tools/render)의 입력
const string Usage = "usage: --map <path> --out <trace.json> [--max-ticks N]";

var values = new Dictionary<string, string>();
for (var i = 0; i + 1 < args.Length; i += 2) values[args[i]] = args[i + 1];
if (args.Length % 2 != 0 || !values.TryGetValue("--map", out var mapPath) || !values.TryGetValue("--out", out var outPath))
{
    Console.Error.WriteLine(Usage);
    return 1;
}
var maxTicks = values.TryGetValue("--max-ticks", out var mt) ? long.Parse(mt, System.Globalization.CultureInfo.InvariantCulture) : 10_000;

var map = MapLoader.LoadFromFile(mapPath);
var places = new List<PlaceInfo>();
places.AddRange(map.Stations.Select(s => new PlaceInfo(s.Id, s.Type, s.X, s.Y)));
places.AddRange(map.Chargers.Select(c => new PlaceInfo(c.Id, "charger", c.X, c.Y)));
places.AddRange(map.Parkings.Select(p => new PlaceInfo(p.Id, "parking", p.X, p.Y)));
var placeById = places.ToDictionary(p => p.Id);

var options = new SimulationOptions { MapPath = mapPath, Goal = default, SpeedFactor = 0, RenderEveryTicks = 0 };
var stepper = new RobotStepper(map, new AStarPathPlanner(), new AlwaysGrantMoveArbiter(), options, new SimulationStats(), renderer: null);

var plans = Scenarios.FactoryA;
var agents = plans.Select(plan =>
{
    var start = placeById[plan.StartId];
    var robot = new Robot { RobotId = plan.RobotId, MapId = map.MapId, Position = new Point(start.X, start.Y), Heading = plan.StartHeading };
    return new Agent(plan, robot, (long)Math.Round(plan.StartDelaySeconds * 1000 / options.TickMs));
}).ToList();

var frames = new List<TickFrame> { Snapshot(0) };
var taskSeq = 0;
for (long tick = 1; tick <= maxTicks && agents.Any(a => a.Phase != Phase.Done); tick++)
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
                AssignNext(a);
                break;
        }

        if (a.Phase != Phase.Running) continue;
        await stepper.StepAsync(a.Robot, tick);

        if (a.Task!.Status == TaskStatus.Failed)
        {
            Console.Error.WriteLine($"tick {tick}: {a.Plan.RobotId} has no path to {a.Plan.Stops[a.Done].PlaceId}");
            return 2;
        }
        if (a.Task.Status != TaskStatus.Completed) continue;

        var stop = a.Plan.Stops[a.Done++];
        a.Robot.Status = placeById[stop.PlaceId].Kind switch
        {
            "pickup" => RobotStatus.Loading,
            "dropoff" => RobotStatus.Unloading,
            "charger" => RobotStatus.Charging,
            _ => RobotStatus.Idle,
        };
        if (a.Done == a.Plan.Stops.Count)
        {
            a.Phase = Phase.Done;
        }
        else
        {
            a.Phase = Phase.Dwelling;
            a.DwellLeft = (long)Math.Round(stop.DwellSeconds * 1000 / options.TickMs);
        }
    }
    frames.Add(Snapshot(tick));
}

if (agents.Any(a => a.Phase != Phase.Done))
{
    Console.Error.WriteLine($"max ticks {maxTicks} reached before all robots finished");
    return 3;
}

var trace = new Trace(
    map.MapId, map.Width, map.Height, GridRows(map), places, options.TickMs, options.MoveMsPerCell, options.TurnMsPer90,
    plans.Select(p => new RobotInfo(p.RobotId, p.StartId, p.Stops.Select(s => s.PlaceId).ToList())).ToList(), frames);

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
await using (var stream = File.Create(outPath))
{
    await JsonSerializer.SerializeAsync(stream, trace, new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    });
}

var conflicts = TraceChecks.FindConflicts(trace);
foreach (var c in conflicts.Take(20)) Console.Error.WriteLine(c);
if (conflicts.Count > 0)
{
    Console.Error.WriteLine($"{conflicts.Count} cell conflicts. change the scenario (trace written for inspection)");
    return 4;
}

Console.WriteLine($"{outPath}: {frames.Count} frames, {frames.Count * options.TickMs / 1000.0:F1}s, {agents.Count} robots, no conflicts");
return 0;

void AssignNext(Agent a)
{
    var place = placeById[a.Plan.Stops[a.Done].PlaceId];
    a.Task = new TransportTask
    {
        TaskId = $"T-{++taskSeq:D5}",
        MapId = map.MapId,
        Type = place.Kind switch { "charger" => TaskType.Charge, "parking" => TaskType.Park, _ => TaskType.Transport },
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
    var goal = a.Phase is Phase.Running ? a.Plan.Stops[a.Done].PlaceId : null;
    return stepper.CurrentAction(r) is { } act
        ? new RobotFrame(r.Position.X, r.Position.Y, (int)r.Heading, r.Status.ToString(), act.Kind.ToString(),
            act.Kind == RobotActionKind.Move ? act.TargetCell.X : null,
            act.Kind == RobotActionKind.Move ? act.TargetCell.Y : null,
            act.Kind == RobotActionKind.Turn ? (int)act.TargetHeading : null,
            Math.Round(Progress(r, act), 3), goal, a.Done)
        : new RobotFrame(r.Position.X, r.Position.Y, (int)r.Heading, r.Status.ToString(), null, null, null, null, null, goal, a.Done);
}).ToList());

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

internal enum Phase { Pending, Running, Dwelling, Done }

internal sealed class Agent(RobotPlan plan, Robot robot, long startTick)
{
    public RobotPlan Plan { get; } = plan;
    public Robot Robot { get; } = robot;
    public long StartTick { get; } = startTick;
    public Phase Phase { get; set; } = Phase.Pending;
    public TransportTask? Task { get; set; }
    public int Done { get; set; }
    public long DwellLeft { get; set; }
}
