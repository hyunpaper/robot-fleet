using System.Text.Json;

namespace RobotFleet.Shared.Messaging;

public static class RoutingKeys
{
    /// telemetry → robotId, Task* → taskId, RobotStatusChanged → robotId, conflict 범주 → mapId
    public static string Of(string eventType, JsonElement payload, string mapId) => eventType switch
    {
        EventType.RobotTelemetry or EventType.RobotStatusChanged => Field(payload, "robotId", eventType),
        EventType.TaskCreated or EventType.TaskAssigned or EventType.TaskStarted
            or EventType.TaskCompleted or EventType.TaskFailed => Field(payload, "taskId", eventType),
        EventType.ConflictDetected or EventType.DeadlockDetected or EventType.PathReplanned => mapId,
        _ => throw new ArgumentException($"알 수 없는 이벤트 타입: {eventType}", nameof(eventType)),
    };

    private static string Field(JsonElement payload, string name, string eventType)
    {
        if (payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString()!;
        }

        throw new ArgumentException($"{eventType} 페이로드에 {name}이(가) 없다", nameof(payload));
    }
}
