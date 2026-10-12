using RobotFleet.Shared.Messaging;

namespace RobotFleet.Shared.Pipeline;

public sealed record OutgoingMessage(EventCategory Category, string RoutingKey, string MessageId,
    string EventType, bool Persistent, ReadOnlyMemory<byte> Body);

public sealed record PublishOutcome(int ReliableConfirmed, int ReliableFailed,
    int TelemetryConfirmed, int TelemetryDropped, IReadOnlyDictionary<string, int> DropReasons);

public interface IEventPublisher
{
    Task<PublishOutcome> PublishBatchAsync(IReadOnlyList<OutgoingMessage> messages, CancellationToken ct);

    /// fleet.invalid 용. 확인 실패 시 예외
    Task PublishToQueueAsync(string queue, OutgoingMessage message, CancellationToken ct);
}
