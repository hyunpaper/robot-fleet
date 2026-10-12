using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RobotFleet.Shared.Messaging;

/// 03 예시처럼 항상 UTC "Z"로 쓴다. 기본 변환기는 오프셋 0을 "+00:00"으로 쓴다
public sealed class UtcDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetDateTimeOffset();

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture));
}
