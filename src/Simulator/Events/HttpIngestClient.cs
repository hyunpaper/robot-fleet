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

    public Task<IngestResult> SendAsync(EventBatch batch, ReadOnlyMemory<byte> body, CancellationToken ct) =>
        throw new NotImplementedException($"{_http}");
}
