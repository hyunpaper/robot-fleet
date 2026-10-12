using System.Globalization;
using System.Text.Json;
using RobotFleet.PipelineHost;
using RobotFleet.Shared.Map;
using RobotFleet.Simulator.Fleet;

// in-process 파이프라인: 인프라 없이 Simulator → Ingest → 브로커 → 워커 → 저장소를 한 프로세스에서 돌리고 대조 결과를 출력한다(10 §9)
// 사용: --map <path> [--robots 20] [--ticks 2000] [--lambda 0.5] [--seed 1] [--workers 1]
var values = new Dictionary<string, string>(StringComparer.Ordinal);
for (var i = 0; i + 1 < args.Length; i += 2)
    values[args[i]] = args[i + 1];
if (!values.TryGetValue("--map", out var mapPath))
{
    Console.Error.WriteLine("usage: --map <path> [--robots 20] [--ticks 2000] [--lambda 0.5] [--seed 1] [--workers 1]");
    return 2;
}

int Int(string key, int fallback) => values.TryGetValue(key, out var v) ? int.Parse(v, CultureInfo.InvariantCulture) : fallback;

var map = MapLoader.LoadFromFile(mapPath);
var workers = Int("--workers", 1);
var fleet = new FleetOptions
{
    Robots = Int("--robots", 20),
    MaxTicks = Int("--ticks", 2000),
    Lambda = values.TryGetValue("--lambda", out var l) ? double.Parse(l, CultureInfo.InvariantCulture) : 0.5,
    Seed = Int("--seed", 1),
    SpeedFactor = 0,
};

await using var pipeline = new InProcessPipeline(map, fleet, sharedRoster: workers > 1);
for (var w = 0; w < workers; w++)
    pipeline.StartWorker($"w-{w}");

var report = await pipeline.RunAsync(TimeSpan.FromSeconds(60));
Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
return report.LossFree && report.InvalidTransitions == 0 ? 0 : 1;
