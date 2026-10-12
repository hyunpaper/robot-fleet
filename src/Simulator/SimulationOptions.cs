using System.Globalization;
using RobotFleet.Shared.Geometry;
using RobotFleet.Shared.Map;

namespace RobotFleet.Simulator;

public sealed record SimulationOptions
{
    public required string MapPath { get; init; }
    public Point Start { get; init; }                // 기본: 첫 번째 Parking
    public Heading StartHeading { get; init; } = Heading.North;
    public required Point Goal { get; init; }
    public int TickMs { get; init; } = 100;
    public double SpeedFactor { get; init; } = 1.0;  // 실시간 대기 = TickMs / SpeedFactor. 0이면 대기 없음
    public int MoveMsPerCell { get; init; } = 1000;  // 1 m/s
    public int TurnMsPer90 { get; init; } = 300;     // 03 문서
    public int RenderEveryTicks { get; init; } = 5;  // 맵 그리기 주기. 0이면 안 그림
    public long MaxTicks { get; init; } = 100_000;   // 안전장치
    public int? Robots { get; init; }                // null이면 M1 단일 로봇 모드
    public double Lambda { get; init; } = 0.5;
    public int Seed { get; init; } = 1;
    public Uri? IngestUrl { get; init; }
    public string? SendLogDirectory { get; init; }
    public int? MetricsPort { get; init; }

    public const string Usage =
        "usage: --map <path> [--start x,y] [--goal x,y] [--heading N|E|S|W] [--speed 1.0] [--tick 100] [--render 5] [--max-ticks N]\n"
        + "       fleet mode: --robots N [--lambda 0.5] [--seed 1] [--ingest url] [--sendlog dir] [--metrics-port p]";

    /// <summary>
    /// 명령행 파싱. --start/--goal 기본값(첫 Parking/첫 Station)이 맵에 의존하므로 맵 로더를 받아 맵도 함께 돌려준다.
    /// 잘못된 인자는 ArgumentException.
    /// </summary>
    public static (SimulationOptions Options, GridMap Map) Parse(string[] args, Func<string, GridMap> loadMap)
    {
        var values = new Dictionary<string, string>();
        for (var i = 0; i < args.Length; i++)
        {
            var key = args[i];
            if (!key.StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"unexpected argument '{key}'");
            if (i + 1 >= args.Length)
                throw new ArgumentException($"{key} requires a value");
            values[key] = args[++i];
        }

        foreach (var key in values.Keys)
        {
            if (key is not ("--map" or "--start" or "--goal" or "--heading" or "--speed" or "--tick" or "--render" or "--max-ticks"
                or "--robots" or "--lambda" or "--seed" or "--ingest" or "--sendlog" or "--metrics-port"))
                throw new ArgumentException($"unknown option '{key}'");
        }

        if (!values.TryGetValue("--map", out var mapPath))
            throw new ArgumentException("--map is required");

        var map = loadMap(mapPath);

        Point start;
        if (values.TryGetValue("--start", out var s)) start = ParsePoint("--start", s);
        else if (map.Parkings.Count > 0) start = new Point(map.Parkings[0].X, map.Parkings[0].Y);
        else throw new ArgumentException("--start is required (map has no parking)");

        Point goal;
        if (values.TryGetValue("--goal", out var g)) goal = ParsePoint("--goal", g);
        else if (map.Stations.Count > 0) goal = new Point(map.Stations[0].X, map.Stations[0].Y);
        else throw new ArgumentException("--goal is required (map has no station)");

        var defaults = new SimulationOptions { MapPath = mapPath, Goal = goal };
        var options = defaults with
        {
            Start = start,
            StartHeading = values.TryGetValue("--heading", out var h) ? ParseHeading(h) : defaults.StartHeading,
            SpeedFactor = values.TryGetValue("--speed", out var sp) ? ParseDouble("--speed", sp, min: 0) : defaults.SpeedFactor,
            TickMs = values.TryGetValue("--tick", out var t) ? (int)ParseLong("--tick", t, min: 1) : defaults.TickMs,
            RenderEveryTicks = values.TryGetValue("--render", out var r) ? (int)ParseLong("--render", r, min: 0) : defaults.RenderEveryTicks,
            MaxTicks = values.TryGetValue("--max-ticks", out var m) ? ParseLong("--max-ticks", m, min: 1) : defaults.MaxTicks,
            Robots = values.TryGetValue("--robots", out var rb) ? (int)ParseLong("--robots", rb, min: 1) : null,
            Lambda = values.TryGetValue("--lambda", out var lm) ? ParseDouble("--lambda", lm, min: 0) : defaults.Lambda,
            Seed = values.TryGetValue("--seed", out var sd) ? (int)ParseLong("--seed", sd, min: int.MinValue) : defaults.Seed,
            IngestUrl = values.TryGetValue("--ingest", out var ig) ? ParseUrl("--ingest", ig) : null,
            SendLogDirectory = values.TryGetValue("--sendlog", out var sl) ? sl : null,
            MetricsPort = values.TryGetValue("--metrics-port", out var mp) ? (int)ParseRange("--metrics-port", mp, 1, 65535) : null,
        };
        return (options, map);
    }

    private static Point ParsePoint(string name, string text)
    {
        var parts = text.Split(',');
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y))
            throw new ArgumentException($"{name} must be 'x,y' but was '{text}'");
        return new Point(x, y);
    }

    private static Heading ParseHeading(string text) => text.ToUpperInvariant() switch
    {
        "N" => Heading.North,
        "E" => Heading.East,
        "S" => Heading.South,
        "W" => Heading.West,
        _ => throw new ArgumentException($"--heading must be N, E, S or W but was '{text}'"),
    };

    private static double ParseDouble(string name, string text, double min)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v < min)
            throw new ArgumentException($"{name} must be a number >= {min.ToString(CultureInfo.InvariantCulture)} but was '{text}'");
        return v;
    }

    private static Uri ParseUrl(string name, string text)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException($"{name} must be an absolute http/https URL but was '{text}'");
        return uri;
    }

    private static long ParseRange(string name, string text, long min, long max)
    {
        var v = ParseLong(name, text, min);
        if (v > max) throw new ArgumentException($"{name} must be an integer between {min} and {max} but was '{text}'");
        return v;
    }

    private static long ParseLong(string name, string text, long min)
    {
        if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) || v < min)
            throw new ArgumentException($"{name} must be an integer >= {min} but was '{text}'");
        return v;
    }
}
