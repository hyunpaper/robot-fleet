using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using RobotFleet.Shared.Messaging;
using RobotFleet.Shared.Pipeline;

namespace RobotFleet.Simulator.Events;

/// POST {baseUri}/api/v1/events/batch. 202 → Accepted/Duplicate, 400 → Invalid, 503 · 5xx · 연결 실패 → Unavailable
public sealed class HttpIngestClient : IIngestClient
{
    private readonly HttpClient _http;

    /// http.BaseAddress = Ingest 주소. 시도 타임아웃은 호출자(송신기)가 CancellationToken으로 건다
    public HttpIngestClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<IngestResult> SendAsync(EventBatch batch, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        using var content = new ReadOnlyMemoryContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await _http.PostAsync("api/v1/events/batch", content, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        return response.StatusCode switch
        {
            HttpStatusCode.Accepted => Accepted(text),
            HttpStatusCode.BadRequest => new IngestResult(IngestStatus.Invalid, Error: text),
            _ => new IngestResult(IngestStatus.Unavailable, Error: $"{(int)response.StatusCode} {text}"),
        };
    }

    private static IngestResult Accepted(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var duplicate = root.TryGetProperty("duplicate", out var d) && d.ValueKind == JsonValueKind.True;
            var dropped = root.TryGetProperty("telemetryDropped", out var t) && t.TryGetInt32(out var n) ? n : 0;
            return new IngestResult(duplicate ? IngestStatus.Duplicate : IngestStatus.Accepted, dropped);
        }
        catch (JsonException)
        {
            return new IngestResult(IngestStatus.Accepted);
        }
    }
}
