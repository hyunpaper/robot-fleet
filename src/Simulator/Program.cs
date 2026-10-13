using System.Globalization;
using System.Text;
using System.Text.Json;
using RobotFleet.FleetController.Arbitration;
using RobotFleet.FleetController.Planning;
using RobotFleet.Shared.Domain;
using RobotFleet.Shared.Map;
using Prometheus;
using RobotFleet.Simulator;
using RobotFleet.Simulator.Events;
using RobotFleet.Simulator.Fleet;

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

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;   // 프로세스를 바로 죽이지 않고 루프가 130으로 끝나게
    cts.Cancel();
};

if (options.Robots is { } robotCount)
{
    var fleetOptions = new FleetOptions
    {
        Robots = robotCount,
        Lambda = options.Lambda,
        Seed = options.Seed,
        TickMs = options.TickMs,
        SpeedFactor = options.SpeedFactor,
        MaxTicks = options.MaxTicks,
    };
    Console.WriteLine($"map {map.MapId} v{map.Version} {map.Width}x{map.Height}  fleet robots {robotCount}");

    using var metricServer = options.MetricsPort is { } metricsPort ? new MetricServer(metricsPort).Start() : null;
    ITickDelay fleetDelay = options.SpeedFactor > 0 ? new RealTimeDelay() : new NoDelay();
    var fleetRenderer = new ConsoleRenderer(Console.Out, map, options.TickMs, options.RenderEveryTicks);

    if (options.IngestUrl is null)
    {
        var noSink = await new FleetSimulation(map, fleetOptions, null, fleetDelay, fleetRenderer).RunAsync(cts.Token);
        return noSink.ExitCode;
    }

    using var http = new HttpClient { BaseAddress = options.IngestUrl };
    var runId = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
    using var sendLog = options.SendLogDirectory is { } logDir ? new SendLog(logDir, map.MapId, runId) : null;
    await using var sender = new EventBatchSender(new HttpIngestClient(http), sendLog, new EventSenderOptions(), TimeProvider.System);

    // 송신 루프는 Ctrl+C와 별개로 Drain이 끝날 때까지 돌린다
    using var senderCts = new CancellationTokenSource();
    var sendLoop = Task.Run(() => sender.RunAsync(senderCts.Token));

    var result = await new FleetSimulation(map, fleetOptions with { RunId = runId }, sender, fleetDelay, fleetRenderer).RunAsync(cts.Token);
    var unsent = await sender.DrainAsync(CancellationToken.None);
    senderCts.Cancel();
    try { await sendLoop; }
    catch (OperationCanceledException) { }

    Console.WriteLine($"unsent {unsent}");
    return unsent > 0 ? 5 : result.ExitCode;
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

ITickDelay delay = options.SpeedFactor > 0 ? new RealTimeDelay() : new NoDelay();
var renderer = new ConsoleRenderer(Console.Out, map, options.TickMs, options.RenderEveryTicks);
var arbiter = new InMemoryMoveArbiter();
arbiter.Register(map.MapId, robot.RobotId, robot.Position);
var loop = new SimulationLoop(map, robot, task, new AStarPathPlanner(), arbiter, options, delay, renderer);
return await loop.RunAsync(cts.Token);
