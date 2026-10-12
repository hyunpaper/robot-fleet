using Microsoft.Extensions.Logging;
using RobotFleet.Shared.Pipeline;

namespace RobotFleet.Infrastructure.RabbitMq;

/// 파티션당 채널 1개 · BasicQos(prefetch) · SAC 큐 구독. 10 §6
public sealed class RabbitPartitionSubscriber : IPartitionSubscriber
{
    private readonly RabbitConnectionProvider _connections;
    private readonly ILogger<RabbitPartitionSubscriber> _logger;

    public RabbitPartitionSubscriber(RabbitConnectionProvider connections, ILogger<RabbitPartitionSubscriber> logger)
    {
        _connections = connections;
        _logger = logger;
    }

    public Task<IPartitionSubscription> SubscribeAsync(string queue, ushort prefetch,
        Func<Delivery, CancellationToken, ValueTask> onDelivery, CancellationToken ct) =>
        throw new NotImplementedException($"{_connections}{_logger}");

    public Task<long> GetMessageCountAsync(string queue, CancellationToken ct) => throw new NotImplementedException();
}
