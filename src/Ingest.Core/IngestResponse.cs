using RobotFleet.Shared.Pipeline;

namespace RobotFleet.Ingest;

/// 202 · 400 · 503 응답 본문. 10 §4
public sealed record IngestResponseBody(string? BatchId, int Accepted, int TelemetryDropped, bool Duplicate, string? Error);

/// StatusCode: 202 Accepted · 400 Invalid · 503 Unavailable. RetryAfterSeconds는 503일 때만
public sealed record IngestResponse(int StatusCode, IngestResponseBody Body, int? RetryAfterSeconds)
{
    /// in-process 클라이언트용 변환
    public IngestResult ToResult() => StatusCode switch
    {
        202 when Body.Duplicate => new IngestResult(IngestStatus.Duplicate, Body.TelemetryDropped),
        202 => new IngestResult(IngestStatus.Accepted, Body.TelemetryDropped),
        400 => new IngestResult(IngestStatus.Invalid, Error: Body.Error),
        _ => new IngestResult(IngestStatus.Unavailable, Error: Body.Error),
    };
}
