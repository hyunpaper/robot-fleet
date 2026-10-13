namespace RobotFleet.Shared.Messaging;

public static class EventType
{
    public const string RobotTelemetry = "RobotTelemetry";
    public const string TaskCreated = "TaskCreated";
    public const string TaskAssigned = "TaskAssigned";
    public const string TaskStarted = "TaskStarted";
    public const string TaskCompleted = "TaskCompleted";
    public const string TaskFailed = "TaskFailed";
    public const string ConflictDetected = "ConflictDetected";
    public const string DeadlockDetected = "DeadlockDetected";
    public const string PathReplanned = "PathReplanned";
    public const string RobotStatusChanged = "RobotStatusChanged";

    public const string SimulationStarted = "SimulationStarted";
    public const string SimulationEnded = "SimulationEnded";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>
    {
        RobotTelemetry, TaskCreated, TaskAssigned, TaskStarted, TaskCompleted, TaskFailed,
        ConflictDetected, DeadlockDetected, PathReplanned, RobotStatusChanged,
        SimulationStarted, SimulationEnded,
    };
}
