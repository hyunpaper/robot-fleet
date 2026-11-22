using System.Text.Json.Serialization;

namespace RobotFleet.Shared.Agent;

/// 에이전트 계약 JSON(LLM 사용자 메시지 · ControlApi 제출). 14 §2.5
/// 열거 문자열 변환기 · TuningKeyJsonConverter 등록은 2단계 S2
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    NumberHandling = JsonNumberHandling.Strict)]
[JsonSerializable(typeof(DeadlockFactSnapshot))]
[JsonSerializable(typeof(InfraIncidentFacts))]
public sealed partial class AgentJsonContext : JsonSerializerContext;
