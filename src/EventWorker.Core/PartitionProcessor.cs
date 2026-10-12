using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using RobotFleet.Shared.Pipeline;

namespace RobotFleet.EventWorker;

/// 파티션 큐 하나: 구독 → N건 또는 T ms 창으로 배치 → 저장(일시 오류는 같은 배치 재시도) → 커밋 뒤 ack. 10 §6
internal sealed class PartitionProcessor
{
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(10);

    private readonly IPartitionSubscriber _subscriber;
    private readonly EventHandlers _handlers;
    private readonly WorkerOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Channel<Delivery> _inbox = Channel.CreateUnbounded<Delivery>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _kill = new();
    private IPartitionSubscription? _subscription;
    private Task _loop = Task.CompletedTask;
    private volatile bool _stopping;

    public PartitionProcessor(string queue, IPartitionSubscriber subscriber, EventHandlers handlers, WorkerOptions options,
                              TimeProvider time, ILogger logger)
    {
        Queue = queue;
        _subscriber = subscriber;
        _handlers = handlers;
        _options = options;
        _time = time;
        _logger = logger;
    }

    public string Queue { get; }

    /// 처리 · ack까지 끝낸 메시지 수(테스트 관찰용)
    public long Acked { get; private set; }

    /// 멈추라고 하지 않았는데 루프가 끝났다(채널이 닫혀 ack 실패 등). 코디네이터가 다시 띄운다
    public bool IsDead => _loop.IsCompleted && !_stopping;

    public async Task StartAsync(CancellationToken ct)
    {
        _subscription = await _subscriber.SubscribeAsync(Queue, _options.Prefetch, OnDeliveryAsync, ct);
        _loop = Task.Run(() => LoopAsync(_kill.Token), CancellationToken.None);
    }

    /// drain = 진행 중 배치를 커밋 · ack한 뒤 채널을 닫는다(남은 미확인분은 브로커가 반환).
    /// drain = false = 강제 종료: ack 없이 바로 채널을 닫는다(프로세스가 죽은 것과 같다)
    public async Task StopAsync(bool drain)
    {
        _stopping = true;
        _inbox.Writer.TryComplete();
        if (drain)
        {
            var finished = await Task.WhenAny(_loop, Task.Delay(DrainTimeout, _time));
            if (finished != _loop)
                _logger.LogWarning("{Queue} 배치 마무리가 {Seconds}초를 넘어 강제 종료", Queue, DrainTimeout.TotalSeconds);
        }

        _kill.Cancel();
        if (_subscription is not null)
            await _subscription.DisposeAsync();
        try
        {
            await _loop;
        }
        catch (Exception ex) when (ex is OperationCanceledException || !drain)
        {
            // 강제 종료 뒤의 ack 실패는 예상된 결과다(미확인분은 브로커가 반환했다)
        }
    }

    private ValueTask OnDeliveryAsync(Delivery delivery, CancellationToken ct)
    {
        _inbox.Writer.TryWrite(delivery);
        return ValueTask.CompletedTask;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var reader = _inbox.Reader;
        while (!_stopping && await reader.WaitToReadAsync(ct))
        {
            var batch = new List<Delivery>(_options.BatchMax);
            var started = _time.GetTimestamp();
            while (batch.Count < _options.BatchMax)
            {
                if (reader.TryRead(out var d))
                {
                    batch.Add(d);
                    continue;
                }

                var remaining = _options.BatchWindow - _time.GetElapsedTime(started);
                if (remaining <= TimeSpan.Zero || _stopping) break;
                using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
                window.CancelAfter(remaining);   // 창 대기는 실시간. 배치 창은 지연 상한일 뿐 정확성과 무관
                try
                {
                    if (!await reader.WaitToReadAsync(window.Token)) break;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    break;
                }
            }

            if (batch.Count == 0) continue;
            try
            {
                await ProcessAsync(batch, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && !ct.IsCancellationRequested)
            {
                // ack · reject 실패 = 채널이 닫혔다. 미확인분은 브로커가 반환하므로 재전달에 맡기고 이 처리기는 끝낸다
                _logger.LogWarning(ex, "{Queue} 브로커 확인 실패, 처리기 종료(재전달에 맡김)", Queue);
                return;
            }
        }
    }

    private async Task ProcessAsync(List<Delivery> batch, CancellationToken ct)
    {
        var started = _time.GetTimestamp();
        var backoff = _options.DbRetryMin;
        BatchOutcome outcome;
        while (true)
        {
            try
            {
                outcome = await _handlers.HandleAsync(batch, ct);
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // ack도 nack도 하지 않는다 — 전달 횟수를 올리지 않고 같은 배치를 다시 (08 §8)
                WorkerMetrics.DbRetry.Inc();
                _logger.LogWarning(ex, "{Queue} 배치 {Count}건 저장 실패, {Delay}ms 후 재시도", Queue, batch.Count, backoff.TotalMilliseconds);
                await Task.Delay(backoff, _time, ct);
                backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, _options.DbRetryMax.Ticks));
            }
        }

        // 커밋이 끝난 뒤에만 브로커에 알린다. 독 메시지는 개별 reject, 나머지는 마지막 tag까지 한 번에 ack
        var subscription = _subscription!;
        foreach (var tag in outcome.Poison)
            await subscription.RejectAsync(tag, ct);
        var poison = outcome.Poison.ToHashSet();
        var last = batch.Where(d => !poison.Contains(d.DeliveryTag)).Select(d => d.DeliveryTag).DefaultIfEmpty(0UL).Max();
        if (last > 0)
            await subscription.AckUpToAsync(last, ct);
        Acked += batch.Count;
        WorkerMetrics.BatchSeconds.Observe(_time.GetElapsedTime(started).TotalSeconds);
    }
}
