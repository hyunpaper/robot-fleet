using RobotFleet.Shared.Pipeline;

namespace RobotFleet.Infrastructure.InMemory;

/// consistent-hash · SAC · prefetch · multiple ack · 채널 닫기 반환 · x-delivery-limit → DLQ ·
/// reject-publish를 실제 의미론대로 흉내 내는 브로커 fake. 10 §12
public sealed class InMemoryBroker : IEventPublisher, IPartitionSubscriber
{
    public InMemoryBroker(string topologyPrefix = "")
    {
        TopologyPrefix = topologyPrefix;
    }

    public string TopologyPrefix { get; }

    public Task<PublishOutcome> PublishBatchAsync(IReadOnlyList<OutgoingMessage> messages, CancellationToken ct) =>
        throw new NotImplementedException();

    public Task PublishToQueueAsync(string queue, OutgoingMessage message, CancellationToken ct) =>
        throw new NotImplementedException();

    public Task<IPartitionSubscription> SubscribeAsync(string queue, ushort prefetch,
        Func<Delivery, CancellationToken, ValueTask> onDelivery, CancellationToken ct) =>
        throw new NotImplementedException();

    public Task<long> GetMessageCountAsync(string queue, CancellationToken ct) => throw new NotImplementedException();
}
