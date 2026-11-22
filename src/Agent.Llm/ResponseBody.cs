using System.Text.Json;

namespace RobotFleet.Agent.Llm;

/// 응답 본문 형식 검사 도우미. 형식 위반은 SchemaViolationException으로 올리고 파서가 잡아 제안 없음으로 바꾼다. 14 §4.2 · 4.3
internal static class ResponseBody
{
    /// 1 · 2단계: 빈 본문 → EmptyResponse, JSON 파싱 실패 → InvalidJson. 성공하면 null
    public static (NoProposalReason Reason, string Detail)? TryOpen(string? text, out JsonDocument? document)
    {
        document = null;
        if (string.IsNullOrWhiteSpace(text))
            return (NoProposalReason.EmptyResponse, "본문 없음");

        try
        {
            // 기본 옵션: 주석 · 뒤 쉼표 · 코드 펜스 · 뒤따르는 글은 모두 실패. 앞뒤 공백만 허용
            document = JsonDocument.Parse(text);
            return null;
        }
        catch (JsonException ex)
        {
            return (NoProposalReason.InvalidJson, ex.Message);
        }
    }

    /// 객체이고 속성 집합이 정확히 names(누락 · 모르는 속성 · 중복 없음)
    public static void RequireObject(JsonElement element, string path, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new SchemaViolationException(path, $"객체여야 한다({element.ValueKind})");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!names.Contains(property.Name, StringComparer.Ordinal))
                throw new SchemaViolationException($"{path}.{property.Name}", "모르는 속성");
            if (!seen.Add(property.Name))
                throw new SchemaViolationException($"{path}.{property.Name}", "중복 속성");
        }

        foreach (var name in names)
        {
            if (!seen.Contains(name))
                throw new SchemaViolationException($"{path}.{name}", "필수 속성 누락");
        }
    }

    public static string String(JsonElement obj, string name, string path)
    {
        var value = obj.GetProperty(name);
        return value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new SchemaViolationException($"{path}.{name}", $"문자열이어야 한다({value.ValueKind})");
    }

    public static IReadOnlyList<string> StringArray(JsonElement obj, string name, string path)
    {
        var value = obj.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Array)
            throw new SchemaViolationException($"{path}.{name}", $"배열이어야 한다({value.ValueKind})");

        var list = new List<string>();
        var i = 0;
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                throw new SchemaViolationException($"{path}.{name}[{i}]", $"문자열이어야 한다({item.ValueKind})");
            list.Add(item.GetString()!);
            i++;
        }

        return list;
    }

    public static double Number(JsonElement obj, string name, string path)
    {
        var value = obj.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Number)
            throw new SchemaViolationException($"{path}.{name}", $"숫자여야 한다({value.ValueKind})");
        if (!value.TryGetDouble(out var number) || !double.IsFinite(number))
            throw new SchemaViolationException($"{path}.{name}", "유한한 실수로 읽을 수 없다");
        return number;
    }
}

/// 메시지 = "{경로}: {문제}". 제안 없음(SchemaViolation)의 Detail이 된다
internal sealed class SchemaViolationException(string path, string problem) : Exception($"{path}: {problem}");
