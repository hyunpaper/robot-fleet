using System.Text.Json;
using System.Text.RegularExpressions;

namespace RobotFleet.Reconcile;

/// RunId: 송신 로그 각 줄의 runId(배치 봉투 값). 없는 줄은 파일명 {mapId}-{runId}.jsonl에서 읽는다(10 §10)
public sealed record SentEvent(string EventId, string BatchId, long Tick, string EventType, string Category, string Key, string RunId);

public static partial class SendLogReader
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// 디렉터리면 *.jsonl 전부(이름 순). 빈 줄은 무시, 깨진 줄 · runId를 알 수 없는 줄은 파일 · 줄 번호를 담아 예외
    public static IEnumerable<SentEvent> Read(string path)
    {
        var files = Directory.Exists(path)
            ? Directory.EnumerateFiles(path, "*.jsonl").Order(StringComparer.Ordinal).ToList()
            : [path];

        foreach (var file in files)
        {
            var fileRunId = RunIdOfFileName(file);
            var lineNumber = 0;
            foreach (var line in File.ReadLines(file))
            {
                lineNumber++;
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                yield return Parse(line, file, lineNumber, fileRunId);
            }
        }
    }

    /// "factory-a-20261013-143022-r200.jsonl" → "20261013-143022-r200". 형식이 아니면 null
    public static string? RunIdOfFileName(string file)
    {
        var match = FileNamePattern().Match(Path.GetFileName(file));
        return match.Success ? match.Groups["run"].Value : null;
    }

    private static SentEvent Parse(string line, string file, int lineNumber, string? fileRunId)
    {
        try
        {
            var row = JsonSerializer.Deserialize<Line>(line, Options);
            if (row is null || row.EventId is null || row.BatchId is null || row.EventType is null
                || row.Category is null || row.Key is null)
            {
                throw new JsonException("필수 필드 누락");
            }

            var runId = string.IsNullOrEmpty(row.RunId) ? fileRunId : row.RunId;
            if (runId is null)
            {
                throw new JsonException("runId 없음(줄에도 파일명에도 없다)");
            }

            return new SentEvent(row.EventId, row.BatchId, row.Tick, row.EventType, row.Category, row.Key, runId);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{file} {lineNumber}번째 줄이 올바르지 않습니다: {ex.Message}", ex);
        }
    }

    private sealed record Line(string? EventId, string? BatchId, long Tick, string? EventType, string? Category, string? Key,
        string? RunId);

    [GeneratedRegex(@"-(?<run>\d{8}-\d{6}-r\d+)\.jsonl$", RegexOptions.CultureInvariant)]
    private static partial Regex FileNamePattern();
}
