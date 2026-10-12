using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RobotFleet.Shared.Messaging;
using RobotFleet.Shared.Pipeline;

namespace RobotFleet.Infrastructure.RabbitMq;

/// 파티션당 채널 1개 · BasicQos(prefetch) · 수동 ack · SAC 큐 구독. 10 §6.
/// 구독 해제 = 취소 + 채널 닫기 → 미확인분은 브로커가 반환한다(취소만으로는 반환되지 않는다)
public sealed class RabbitPartitionSubscriber : IPartitionSubscriber
{
    private readonly RabbitConnectionProvider _connections;
    private readonly ILogger<RabbitPartitionSubscriber> _logger;

    public RabbitPartitionSubscriber(RabbitConnectionProvider connections, ILogger<RabbitPartitionSubscriber> logger)
    {
        _connections = connections;
        _logger = logger;
    }

    public async Task<IPartitionSubscription> SubscribeAsync(string queue, ushort prefetch,
        Func<Delivery, CancellationToken, ValueTask> onDelivery, CancellationToken ct)
    {
        var connection = await _connections.GetAsync(ct);
        var channel = await connection.CreateChannelAsync(cancellationToken: ct);
        await channel.BasicQosAsync(0, prefetch, global: false, ct);
        var cts = new CancellationTokenSource();
        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, e) =>
        {
            // 본문 버퍼는 핸들러가 끝나면 재사용된다 — 복사한다
            var delivery = new Delivery(queue, e.DeliveryTag, e.BasicProperties.MessageId ?? "", e.BasicProperties.Type ?? "",
                HeaderInt(e.BasicProperties.Headers, "x-schema-version") ?? FleetSchema.Version, e.Body.ToArray(), e.Redelivered,
                HeaderInt(e.BasicProperties.Headers, "x-delivery-count") ?? (e.Redelivered ? 1 : 0));
            await onDelivery(delivery, cts.Token);
        };
        var tag = await channel.BasicConsumeAsync(queue, autoAck: false, consumer, ct);
        _logger.LogDebug("{Queue} 구독(prefetch {Prefetch})", queue, prefetch);
        return new Subscription(queue, channel, tag, cts, _logger);
    }

    public async Task<long> GetMessageCountAsync(string queue, CancellationToken ct)
    {
        var connection = await _connections.GetAsync(ct);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);
        var ok = await channel.QueueDeclarePassiveAsync(queue, ct);
        return ok.MessageCount;
    }

    private static int? HeaderInt(IDictionary<string, object?>? headers, string key)
    {
        if (headers is null || !headers.TryGetValue(key, out var value)) return null;
        if (value is null || value is byte[]) return null;   // 문자열 헤더는 byte[]로 온다
        return Convert.ToInt32(value);
    }

    private sealed class Subscription(string queue, IChannel channel, string consumerTag, CancellationTokenSource cts, ILogger logger)
        : IPartitionSubscription
    {
        public string Queue => queue;

        public Task AckUpToAsync(ulong deliveryTag, CancellationToken ct) =>
            channel.BasicAckAsync(deliveryTag, multiple: true, ct).AsTask();

        public Task RejectAsync(ulong deliveryTag, CancellationToken ct) =>
            channel.BasicRejectAsync(deliveryTag, requeue: false, ct).AsTask();

        public async ValueTask DisposeAsync()
        {
            await cts.CancelAsync();
            try
            {
                if (channel.IsOpen)
                {
                    await channel.BasicCancelAsync(consumerTag);
                    await channel.CloseAsync();
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "{Queue} 구독 해제 중 오류(채널이 이미 닫힘)", queue);
            }

            await channel.DisposeAsync();
            cts.Dispose();
        }
    }
}
