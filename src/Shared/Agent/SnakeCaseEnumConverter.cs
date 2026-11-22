using System.Text.Json;
using System.Text.Json.Serialization;

namespace RobotFleet.Shared.Agent;

/// 열거 ↔ snake_case 문자열(`cycle_wait`). 숫자 입력은 거부한다. 14 §2.5
public sealed class SnakeCaseEnumConverter<T>() : JsonStringEnumConverter<T>(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false)
    where T : struct, Enum;
