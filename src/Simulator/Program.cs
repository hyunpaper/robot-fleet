using System.Text;
using System.Text.Json;
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

// TODO(core): Robot/TransportTask 생성 → SimulationLoop(map, robot, task, new AStarPathPlanner(),
//             new AlwaysGrantMoveArbiter(), options, delay, renderer).RunAsync(ct) 연결, Ctrl+C는 130
return 0;
