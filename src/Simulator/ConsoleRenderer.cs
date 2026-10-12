using System.Globalization;
using System.Text;
using RobotFleet.FleetController.Arbitration;
using RobotFleet.FleetController.Planning;
using RobotFleet.Shared.Domain;
using RobotFleet.Shared.Geometry;
using RobotFleet.Shared.Map;

namespace RobotFleet.Simulator;

/// <summary>이벤트 로그(상태·동작이 바뀔 때만 한 줄)와 로봇 중심 21×11 맵 창을 출력한다.</summary>
public sealed class ConsoleRenderer
{
    private const int WindowWidth = 21;
    private const int WindowHeight = 11;

    private readonly TextWriter _out;
    private readonly GridMap _map;
    private readonly int _tickMs;
    private readonly int _renderEveryTicks;

    public ConsoleRenderer(TextWriter output, GridMap map, int tickMs, int renderEveryTicks)
    {
        _out = output;
        _map = map;
        _tickMs = tickMs;
        _renderEveryTicks = renderEveryTicks;
    }

    // ---- 이벤트 로그 ----

    public void LogPlan(long tick, string robotId, Point start, Heading heading, Point goal, PathResult result) =>
        Line(tick, robotId, "PLAN",
            string.Create(CultureInfo.InvariantCulture,
                $"{start}{heading.ToChar()} → {goal}   steps={result.MoveCount} cost={result.Cost:F1} expanded={result.ExpandedNodes} in {result.Elapsed.TotalMilliseconds:F1}ms"));

    public void LogMove(long tick, string robotId, Point from, Point to) =>
        Line(tick, robotId, "MOVE", $"{from} → {to}");

    public void LogTurn(long tick, string robotId, Heading from, Heading to) =>
        Line(tick, robotId, "TURN", $"{from.ToChar()} → {to.ToChar()}");

    public void LogArrived(long tick, string robotId, Point cell, long ticks, int moves, int turns, int overrun) =>
        Line(tick, robotId, "ARRIVED", $"{cell}   ticks={ticks} moves={moves} turns={turns} overrun={overrun}");

    public void LogNoPath(long tick, string robotId, Point start, Point goal, PathStatus status) =>
        Line(tick, robotId, "NOPATH", $"{start} → {goal}   status={status}");

    public void LogWait(long tick, string robotId, Point cell, MoveDenyReason reason) =>
        Line(tick, robotId, "WAIT", $"{cell}   reason={reason}");

    private void Line(long tick, string robotId, string kind, string text)
    {
        var seconds = (tick * _tickMs / 1000.0).ToString("F1", CultureInfo.InvariantCulture);
        _out.WriteLine($"[t={tick:D6} {seconds}s] {robotId} {kind,-7} {text}");
    }

    // ---- 맵 렌더 ----

    /// <summary>매 틱 끝에 호출. RenderEveryTicks 주기에만 맵 창을 그린다.</summary>
    public void OnTick(long tick, Robot robot)
    {
        if (_renderEveryTicks <= 0 || tick % _renderEveryTicks != 0) return;
        _out.Write(RenderWindow(tick, robot));
    }

    public string RenderWindow(long tick, Robot robot)
    {
        var path = new HashSet<Point>();
        for (var i = robot.PathIndex + 1; i < robot.PlannedPath.Count; i++)
            path.Add(robot.PlannedPath[i].Cell);

        var left = robot.Position.X - WindowWidth / 2;
        var top = robot.Position.Y - WindowHeight / 2;
        var border = new string('─', WindowWidth * 2 + 1);

        var sb = new StringBuilder();
        sb.Append("  ┌").Append(border).AppendLine("┐");
        for (var row = 0; row < WindowHeight; row++)
        {
            sb.Append("  │");
            for (var col = 0; col < WindowWidth; col++)
            {
                var p = new Point(left + col, top + row);
                sb.Append(' ').Append(Glyph(p, robot, path));
            }
            sb.AppendLine(" │");
        }
        sb.Append("  └").Append(border).AppendLine("┘");

        var last = Math.Max(0, robot.PlannedPath.Count - 1);
        sb.AppendLine($"  tick {tick:D6}  pos {robot.Position} {robot.Heading.ToChar()}  {robot.Status}  path {robot.PathIndex}/{last}");
        return sb.ToString();
    }

    private char Glyph(Point p, Robot robot, HashSet<Point> path)
    {
        if (p == robot.Position) return "^>v<"[(int)robot.Heading];
        if (!_map.InBounds(p)) return ' ';
        if (path.Contains(p)) return '*';
        return _map[p] switch
        {
            CellType.Blocked => '#',
            CellType.Aisle => '.',
            CellType.Station => 'S',
            CellType.Charge => 'C',
            CellType.Parking => 'P',
            _ => '?',
        };
    }
}
