using System.Text;
using System.Text.Json;
using RobotFleet.FleetController.Arbitration;
using RobotFleet.FleetController.Planning;
using RobotFleet.Shared.Domain;
using RobotFleet.Shared.Map;
using RobotFleet.Simulator;

Console.OutputEncoding = new UTF8Encoding(false);

SimulationOptions options;
GridMap map;
try
{
    (options, map) = SimulationOptions.Parse(args, MapLoader.LoadFromFile);
}
catch (MapValidationException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
catch (Exception ex) when (ex is ArgumentException or IOException or JsonException)
{
    Console.Error.WriteLine(ex.Message);
    if (ex is ArgumentException) Console.Error.WriteLine(SimulationOptions.Usage);
    return 1;
}

Console.WriteLine($"map {map.MapId} v{map.Version} {map.Width}x{map.Height}  start {options.Start}  goal {options.Goal}");

var robot = new Robot
{
    RobotId = "R-001",
    MapId = map.MapId,
    Position = options.Start,
    Heading = options.StartHeading,
};
var task = new TransportTask
{
    TaskId = "T-00001",
    MapId = map.MapId,
    Type = TaskType.Park,          // M1: 목적지로 가서 멈춘다
    Origin = options.Start,
    Destination = options.Goal,
    CreatedAt = DateTimeOffset.UtcNow,
};

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;   // 프로세스를 바로 죽이지 않고 루프가 130으로 끝나게
    cts.Cancel();
};

ITickDelay delay = options.SpeedFactor > 0 ? new RealTimeDelay() : new NoDelay();
var renderer = new ConsoleRenderer(Console.Out, map, options.TickMs, options.RenderEveryTicks);
var arbiter = new InMemoryMoveArbiter();
arbiter.Register(map.MapId, robot.RobotId, robot.Position);
var loop = new SimulationLoop(map, robot, task, new AStarPathPlanner(), arbiter, options, delay, renderer);
return await loop.RunAsync(cts.Token);
