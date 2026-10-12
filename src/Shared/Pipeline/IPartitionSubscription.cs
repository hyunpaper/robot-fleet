namespace RobotFleet.Shared.Pipeline;

public sealed record Delivery(string Queue, ulong DeliveryTag, string MessageId, string EventType,
    int SchemaVersion, ReadOnlyMemory<byte> Body, bool Redelivered, long DeliveryCount);

/// Dispose = 취소 + 채널 닫기(미확인 반환)
public interface IPartitionSubscription : IAsyncDisposable
{
    string Queue { get; }

    /// multiple: true
    Task AckUpToAsync(ulong deliveryTag, CancellationToken ct);

    /// requeue: false → DLX
    Task RejectAsync(ulong deliveryTag, CancellationToken ct);
}
