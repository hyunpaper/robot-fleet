using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace RobotFleet.Shared.Messaging;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    NumberHandling = JsonNumberHandling.Strict,
    Converters = [typeof(UtcDateTimeOffsetConverter)])]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(EventBatch))]
[JsonSerializable(typeof(EventEnvelope))]
[JsonSerializable(typeof(RobotTelemetryPayload))]
[JsonSerializable(typeof(TaskCreatedPayload))]
[JsonSerializable(typeof(TaskAssignedPayload))]
[JsonSerializable(typeof(TaskStartedPayload))]
[JsonSerializable(typeof(TaskCompletedPayload))]
[JsonSerializable(typeof(TaskFailedPayload))]
[JsonSerializable(typeof(ConflictDetectedPayload))]
[JsonSerializable(typeof(DeadlockDetectedPayload))]
[JsonSerializable(typeof(PathReplannedPayload))]
[JsonSerializable(typeof(RobotStatusChangedPayload))]
[JsonSerializable(typeof(SimulationStartedPayload))]
[JsonSerializable(typeof(SimulationEndedPayload))]
public sealed partial class FleetJsonContext : JsonSerializerContext;

public static class FleetJson
{
    public static JsonSerializerOptions Options => FleetJsonContext.Default.Options;

    public static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, TypeInfo<T>());

    public static T Deserialize<T>(ReadOnlySpan<byte> utf8) =>
        JsonSerializer.Deserialize(utf8, TypeInfo<T>())
        ?? throw new JsonException($"{typeof(T).Name}을(를) null로 읽을 수 없다");

    public static JsonElement ToElement<T>(T payload) => JsonSerializer.SerializeToElement(payload, TypeInfo<T>());

    public static T PayloadOf<T>(EventEnvelope envelope) =>
        envelope.Payload.Deserialize(TypeInfo<T>())
        ?? throw new JsonException($"{typeof(T).Name} 페이로드가 null이다");

    private static JsonTypeInfo<T> TypeInfo<T>() => (JsonTypeInfo<T>)Options.GetTypeInfo(typeof(T));
}
