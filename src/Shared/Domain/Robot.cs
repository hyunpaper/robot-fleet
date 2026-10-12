using RobotFleet.Shared.Geometry;

namespace RobotFleet.Shared.Domain;

public sealed class Robot
{
    public required string RobotId { get; init; }        // "R-001"
    public required string MapId { get; init; }
    public Point Position { get; set; }
    public Heading Heading { get; set; }
    public RobotStatus Status { get; set; } = RobotStatus.Idle;
    public string? CurrentTaskId { get; set; }
    public int BatteryPercent { get; set; } = 100;
    public IReadOnlyList<PathStep> PlannedPath { get; set; } = Array.Empty<PathStep>();
    /// <summary>PlannedPath에서 현재 서 있는 칸의 인덱스. 도착하면 PlannedPath.Count - 1.</summary>
    public int PathIndex { get; set; }
}
