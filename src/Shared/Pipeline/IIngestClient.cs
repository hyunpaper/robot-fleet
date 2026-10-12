using RobotFleet.Shared.Messaging;

namespace RobotFleet.Shared.Pipeline;

public enum IngestStatus { Accepted, Duplicate, Unavailable, Invalid }

public sealed record IngestResult(IngestStatus Status, int TelemetryDropped = 0, string? Error = null);

public interface IIngestClient
{
    Task<IngestResult> SendAsync(EventBatch batch, ReadOnlyMemory<byte> body, CancellationToken ct);
}
