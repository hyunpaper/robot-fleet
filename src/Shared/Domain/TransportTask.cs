using RobotFleet.Shared.Geometry;

namespace RobotFleet.Shared.Domain;

public sealed class TransportTask
{
    public required string TaskId { get; init; }        // "T-00001"
    public required string MapId { get; init; }
    public TaskType Type { get; init; } = TaskType.Transport;
    public Point Origin { get; init; }
    public Point Destination { get; init; }
    public int Priority { get; init; }                  // 0 = normal, 1 = high
    public DateTimeOffset CreatedAt { get; init; }
    public string? AssignedRobotId { get; set; }
    public TaskStatus Status { get; set; } = TaskStatus.Pending;
}
