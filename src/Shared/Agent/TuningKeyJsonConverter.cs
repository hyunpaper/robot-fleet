using System.Text.Json;
using System.Text.Json.Serialization;
using RobotFleet.Shared.Tuning;

namespace RobotFleet.Shared.Agent;

/// TuningKey ↔ 카탈로그 Name(`priority.wait_weight`). 모르는 이름은 JsonException. 14 §2.5
public sealed class TuningKeyJsonConverter : JsonConverter<TuningKey>
{
    public override TuningKey Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"TuningKey는 문자열이어야 한다(토큰 {reader.TokenType}).");
        }

        var name = reader.GetString()!;
        if (!TuningCatalog.TryParseName(name, out var key))
        {
            throw new JsonException($"알 수 없는 설정 키: {name}");
        }

        return key;
    }

    public override void Write(Utf8JsonWriter writer, TuningKey value, JsonSerializerOptions options) =>
        writer.WriteStringValue(TuningCatalog.Get(value).Name);
}
