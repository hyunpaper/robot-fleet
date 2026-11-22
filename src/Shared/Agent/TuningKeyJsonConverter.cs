using System.Text.Json;
using System.Text.Json.Serialization;
using RobotFleet.Shared.Tuning;

namespace RobotFleet.Shared.Agent;

/// TuningKey ↔ 카탈로그 Name(`priority.wait_weight`). 모르는 이름은 JsonException. 14 §2.5
public sealed class TuningKeyJsonConverter : JsonConverter<TuningKey>
{
    public override TuningKey Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotImplementedException();

    public override void Write(Utf8JsonWriter writer, TuningKey value, JsonSerializerOptions options) =>
        throw new NotImplementedException();
}
