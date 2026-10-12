namespace RobotFleet.Ingest;

/// 202 · 400 · 503 응답 본문. 10 §4
public sealed record IngestResponseBody(string? BatchId, int Accepted, int TelemetryDropped, bool Duplicate, string? Error);

/// StatusCode: 202 Accepted · 400 Invalid · 503 Unavailable. RetryAfterSeconds는 503일 때만
public sealed record IngestResponse(int StatusCode, IngestResponseBody Body, int? RetryAfterSeconds);
