using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RobotFleet.Shared.Messaging;
using RobotFleet.Shared.Pipeline;

namespace RobotFleet.Infrastructure.RabbitMq;

/// 배열 순서대로 직렬 송신, 확인은 시퀀스 번호로 몰아 받는다(메시지마다 왕복을 기다리지 않는다). 10 §4.
/// 채널 하나를 배치 하나가 독점한다 — 시퀀스 번호가 채널 단위라 섞이면 부분 실패 판정이 어긋난다
public sealed class RabbitEventPublisher : IEventPublisher, IAsyncDisposable
{
    private readonly RabbitConnectionProvider _connections;
    private readonly RabbitMqOptions _options;
    private readonly ILogger<RabbitEventPublisher> _logger;
    private readonly ChannelPool _reliable;
    private readonly ChannelPool _telemetry;

    public RabbitEventPublisher(RabbitConnectionProvider connections, RabbitMqOptions options, ILogger<RabbitEventPublisher> logger)
    {
        _connections = connections;
        _options = options;
        _logger = logger;
        _reliable = new ChannelPool(connections, options.PublishChannelsPerCategory, logger);
        _telemetry = new ChannelPool(connections, options.PublishChannelsPerCategory, logger);
    }

    public async Task<PublishOutcome> PublishBatchAsync(IReadOnlyList<OutgoingMessage> messages, CancellationToken ct)
    {
        var reliable = messages.Where(m => m.Category != EventCategory.Telemetry).ToList();
        var telemetry = messages.Where(m => m.Category == EventCategory.Telemetry).ToList();
        var reliableTask = PublishAsync(_reliable, reliable, _options.ReliableConfirmTimeout, ct);
        var telemetryTask = PublishAsync(_telemetry, telemetry, _options.TelemetryConfirmTimeout, ct);
        var (relOk, relFailures) = await reliableTask;
        var (telOk, telFailures) = await telemetryTask;
        var reasons = telFailures.GroupBy(r => r).ToDictionary(g => g.Key, g => g.Count());
        return new PublishOutcome(relOk, relFailures.Count, telOk, telFailures.Count, reasons);
    }

    public async Task PublishToQueueAsync(string queue, OutgoingMessage message, CancellationToken ct)
    {
        var lease = await _reliable.RentAsync(ct);
        try
        {
            var confirm = await lease.PublishAsync("", queue, message, ct);
            var result = await confirm.WaitAsync(_options.ReliableConfirmTimeout, ct);
            if (!result) throw new InvalidOperationException($"{queue} 발행이 nack 되었다");
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not InvalidOperationException)
        {
            lease.Broken = true;
            throw new InvalidOperationException($"{queue} 발행 확인 실패", ex);
        }
        finally
        {
            _reliable.Return(lease);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _reliable.DisposeAsync();
        await _telemetry.DisposeAsync();
    }

    // 반환: (확인 수, 실패 사유 목록)
    private async Task<(int Ok, List<string> Failures)> PublishAsync(ChannelPool pool, List<OutgoingMessage> messages,
        TimeSpan timeout, CancellationToken ct)
    {
        var failures = new List<string>();
        if (messages.Count == 0) return (0, failures);

        var lease = await pool.RentAsync(ct);
        var confirms = new List<Task<bool>>(messages.Count);
        try
        {
            foreach (var m in messages)
            {
                var exchange = TopologySpec.ExchangeName(m.Category, _options.TopologyPrefix);
                confirms.Add(await lease.PublishAsync(exchange, m.RoutingKey, m, ct));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // 채널이 끊겼다: 확인을 못 받은 것은 들어갔는지 모른다 → 실패로 보고 재전송에 맡긴다(중복은 워커가 막는다)
            _logger.LogWarning(ex, "발행 중 채널 오류, {Sent}/{Total}건까지 송신", confirms.Count, messages.Count);
            lease.Broken = true;
        }

        var ok = 0;
        try
        {
            await Task.WhenAll(confirms).WaitAsync(timeout, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // 타임아웃 · 채널 닫힘: 아래에서 확인별로 센다
        }

        foreach (var c in confirms)
        {
            if (c.IsCompletedSuccessfully && c.Result) ok++;
            else failures.Add(c.IsCompletedSuccessfully ? "nack" : c.IsCompleted ? "channel_closed" : "timeout");
        }

        failures.AddRange(Enumerable.Repeat("channel_closed", messages.Count - confirms.Count));
        lease.ForgetPending();
        pool.Return(lease);
        return (ok, failures);
    }

    private sealed class ChannelPool(RabbitConnectionProvider connections, int size, ILogger logger) : IAsyncDisposable
    {
        private readonly SemaphoreSlim _slots = new(size, size);
        private readonly ConcurrentBag<ConfirmChannel> _idle = [];

        public async Task<ConfirmChannel> RentAsync(CancellationToken ct)
        {
            await _slots.WaitAsync(ct);
            try
            {
                while (_idle.TryTake(out var c))
                {
                    if (c.IsOpen && !c.Broken) return c;
                    await c.DisposeAsync();
                }

                var connection = await connections.GetAsync(ct);
                return await ConfirmChannel.CreateAsync(connection, logger, ct);
            }
            catch
            {
                _slots.Release();
                throw;
            }
        }

        public void Return(ConfirmChannel channel)
        {
            if (channel.IsOpen && !channel.Broken) _idle.Add(channel);
            else _ = channel.DisposeAsync().AsTask();
            _slots.Release();
        }

        public async ValueTask DisposeAsync()
        {
            while (_idle.TryTake(out var c))
                await c.DisposeAsync();
        }
    }

    /// 확인 모드(추적은 직접) 채널: 시퀀스 번호 → 확인 대기
    private sealed class ConfirmChannel : IAsyncDisposable
    {
        private readonly IChannel _channel;
        private readonly ConcurrentDictionary<ulong, TaskCompletionSource<bool>> _pending = new();

        private ConfirmChannel(IChannel channel)
        {
            _channel = channel;
            channel.BasicAcksAsync += (_, e) => Settle(e.DeliveryTag, e.Multiple, true);
            channel.BasicNacksAsync += (_, e) => Settle(e.DeliveryTag, e.Multiple, false);
            channel.ChannelShutdownAsync += (_, _) =>
            {
                foreach (var tcs in _pending.Values) tcs.TrySetException(new InvalidOperationException("채널 닫힘"));
                _pending.Clear();
                return Task.CompletedTask;
            };
        }

        public bool IsOpen => _channel.IsOpen;

        public bool Broken { get; set; }

        public static async Task<ConfirmChannel> CreateAsync(IConnection connection, ILogger logger, CancellationToken ct)
        {
            var channel = await connection.CreateChannelAsync(
                new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: false), ct);
            logger.LogDebug("발행 채널 {Number} 열림", channel.ChannelNumber);
            return new ConfirmChannel(channel);
        }

        public async Task<Task<bool>> PublishAsync(string exchange, string routingKey, OutgoingMessage m, CancellationToken ct)
        {
            var seq = await _channel.GetNextPublishSequenceNumberAsync(ct);
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[seq] = tcs;
            var props = new BasicProperties
            {
                MessageId = m.MessageId,
                Type = m.EventType,
                ContentType = "application/json",
                DeliveryMode = m.Persistent ? DeliveryModes.Persistent : DeliveryModes.Transient,
                Headers = new Dictionary<string, object?> { ["x-schema-version"] = FleetSchema.Version },
            };
            await _channel.BasicPublishAsync(exchange, routingKey, mandatory: false, props, m.Body, ct);
            return tcs.Task;
        }

        /// 타임아웃으로 포기한 확인은 잊는다(늦게 온 확인은 무시)
        public void ForgetPending() => _pending.Clear();

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (_channel.IsOpen) await _channel.CloseAsync();
            }
            catch (Exception)
            {
                // 이미 끊긴 채널
            }

            await _channel.DisposeAsync();
        }

        private Task Settle(ulong tag, bool multiple, bool ack)
        {
            if (multiple)
            {
                foreach (var key in _pending.Keys.Where(k => k <= tag))
                {
                    if (_pending.TryRemove(key, out var t)) t.TrySetResult(ack);
                }
            }
            else if (_pending.TryRemove(tag, out var t))
            {
                t.TrySetResult(ack);
            }

            return Task.CompletedTask;
        }
    }
}
