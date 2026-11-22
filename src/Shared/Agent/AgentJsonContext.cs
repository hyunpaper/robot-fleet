using System.Text.Json.Serialization;

namespace RobotFleet.Shared.Agent;

/// 에이전트 계약 JSON(LLM 사용자 메시지 · ControlApi 제출). 14 §2.5
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    NumberHandling = JsonNumberHandling.Strict,
    Converters =
    [
        typeof(TuningKeyJsonConverter),
        typeof(SnakeCaseEnumConverter<DeadlockCauseType>),
        typeof(SnakeCaseEnumConverter<RunMetric>),
        typeof(SnakeCaseEnumConverter<PastActionOutcome>),
        typeof(SnakeCaseEnumConverter<InfraIncidentKind>),
        typeof(SnakeCaseEnumConverter<InfraFactSource>),
        typeof(InfraActionIdJsonConverter),
    ])]
[JsonSerializable(typeof(DeadlockFactSnapshot))]
[JsonSerializable(typeof(InfraIncidentFacts))]
public sealed partial class AgentJsonContext : JsonSerializerContext;
