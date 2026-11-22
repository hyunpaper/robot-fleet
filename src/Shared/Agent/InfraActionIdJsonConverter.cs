using System.Text.Json.Serialization;

namespace RobotFleet.Shared.Agent;

/// InfraActionId는 이름 그대로(`I1`, `B3`). 숫자 입력은 거부한다. 14 §2.5
public sealed class InfraActionIdJsonConverter() : JsonStringEnumConverter<InfraActionId>(null, allowIntegerValues: false);
