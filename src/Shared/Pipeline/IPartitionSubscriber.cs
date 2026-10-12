namespace RobotFleet.Shared.Pipeline;

public interface IPartitionSubscriber
{
    Task<IPartitionSubscription> SubscribeAsync(string queue, ushort prefetch,
        Func<Delivery, CancellationToken, ValueTask> onDelivery, CancellationToken ct);

    Task<long> GetMessageCountAsync(string queue, CancellationToken ct);
}
