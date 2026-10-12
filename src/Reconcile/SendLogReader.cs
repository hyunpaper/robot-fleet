using System.Text.Json;

namespace RobotFleet.Reconcile;

public sealed record SentEvent(string EventId, string BatchId, long Tick, string EventType, string Category, string Key);

public static class SendLogReader
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// 디렉터리면 *.jsonl 전부(이름 순). 빈 줄은 무시, 깨진 줄은 파일 · 줄 번호를 담아 예외
    public static IEnumerable<SentEvent> Read(string path)
    {
        var files = Directory.Exists(path)
            ? Directory.EnumerateFiles(path, "*.jsonl").Order(StringComparer.Ordinal).ToList()
            : [path];

        foreach (var file in files)
        {
            var lineNumber = 0;
            foreach (var line in File.ReadLines(file))
            {
                lineNumber++;
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                yield return Parse(line, file, lineNumber);
            }
        }
    }

    private static SentEvent Parse(string line, string file, int lineNumber)
    {
        try
        {
            var row = JsonSerializer.Deserialize<SentEvent>(line, Options);
            if (row is null || row.EventId is null || row.BatchId is null || row.EventType is null
                || row.Category is null || row.Key is null)
            {
                throw new JsonException("필수 필드 누락");
            }

            return row;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{file} {lineNumber}번째 줄이 올바르지 않습니다: {ex.Message}", ex);
        }
    }
}
