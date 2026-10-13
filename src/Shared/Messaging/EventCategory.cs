namespace RobotFleet.Shared.Messaging;

public enum EventCategory { Telemetry, Task, Conflict }

public static class EventCategories
{
    public static EventCategory Of(string eventType) => eventType switch
    {
        EventType.RobotTelemetry => EventCategory.Telemetry,
        EventType.TaskCreated or EventType.TaskAssigned or EventType.TaskStarted
            or EventType.TaskCompleted or EventType.TaskFailed or EventType.RobotStatusChanged
            or EventType.SimulationStarted or EventType.SimulationEnded => EventCategory.Task,
        EventType.ConflictDetected or EventType.DeadlockDetected or EventType.PathReplanned => EventCategory.Conflict,
        _ => throw new ArgumentException($"알 수 없는 이벤트 타입: {eventType}", nameof(eventType)),
    };

    // 03 매핑 표: telemetry와 PathReplanned만 비영속
    public static bool IsPersistent(string eventType)
    {
        var category = Of(eventType);
        return category != EventCategory.Telemetry && eventType != EventType.PathReplanned;
    }
}
