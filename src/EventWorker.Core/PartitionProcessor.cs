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
    private bool _storeHalted;

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
        var outcome = await HandleWithIsolationAsync(batch, ct);

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

    /// 저장 오류를 갈래로 가른다(10 §6).
    /// 일시: ack · nack 없이 상한 백오프로 재시도(전달 횟수 보존, 08 §8).
    /// 데이터: 배치를 절반씩 나눠 원인을 1건까지 좁혀 **배치당 상한 1건**만 DLQ, 나머지는 적재.
    /// 시스템 · 데이터 상한 초과: 분할 없이 같은 배치를 재시도하며 **파티션 정지 유지 + 경보** — DB가 고쳐지면 자동 재개.
    /// 결정적 예외는 커밋 전에 던져지고 트랜잭션이 롤백돼 있어 분할 · 재시도가 안전하다(멱등)
    private async Task<BatchOutcome> HandleWithIsolationAsync(IReadOnlyList<Delivery> batch, CancellationToken ct)
    {
        var backoff = _options.DbRetryMin;
        while (true)
        {
            try
            {
                var outcome = await _handlers.HandleAsync(batch, ct);
                SetHalted(false, null, null);
                return outcome;
            }
            catch (ClassifiedStoreException ex) when (ex.Kind == StoreErrorKind.Data && !ct.IsCancellationRequested)
            {
                var budget = new[] { 1 };   // 원래 배치당 DLQ 상한 1건
                var result = await TryIsolateDataAsync(batch, budget, ct);
                if (result.Ok)
                {
                    if (result.DataDlq > 0) WorkerMetrics.DataErrorDlq.Inc(result.DataDlq);
                    SetHalted(false, null, null);
                    return result.Outcome;
                }

                // 상한 초과(또는 분할 중 시스템 오류) → 정지로 전환
                SetHalted(true, ex.SqlState, ex);
                backoff = await DelayBackoffAsync(backoff, ct);
            }
            catch (ClassifiedStoreException ex) when (ex.Kind == StoreErrorKind.System && !ct.IsCancellationRequested)
            {
                // 시스템 오류: 분할하지 않고 정지 유지, DB가 고쳐지면 다음 재시도에서 통과
                SetHalted(true, ex.SqlState, ex);
                backoff = await DelayBackoffAsync(backoff, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && !ct.IsCancellationRequested)
            {
                // 일시 오류 — ack도 nack도 하지 않고 같은 배치를 다시 (08 §8)
                WorkerMetrics.DbRetry.Inc();
                _logger.LogWarning(ex, "{Queue} 배치 {Count}건 저장 일시 오류, {Delay}ms 후 재시도", Queue, batch.Count, backoff.TotalMilliseconds);
                backoff = await DelayBackoffAsync(backoff, ct);
            }
        }
    }

    /// 데이터 오류 분할 격리. 반환 Ok=false면 DLQ 상한 초과 또는 하위에서 시스템 오류 — 상위가 정지로 전환한다.
    /// 아직 브로커에 reject하지 않으므로(ProcessAsync가 끝에 한다) 정지 전환은 유실 없이 안전하다
    private async Task<IsolationResult> TryIsolateDataAsync(IReadOnlyList<Delivery> batch, int[] budget, CancellationToken ct)
    {
        var backoff = _options.DbRetryMin;
        while (true)
        {
            try
            {
                return new IsolationResult(await _handlers.HandleAsync(batch, ct), true, 0);
            }
            catch (ClassifiedStoreException ex) when (ex.Kind == StoreErrorKind.System && !ct.IsCancellationRequested)
            {
                return new IsolationResult(EmptyOutcome, false, 0);   // 상위가 정지 전환
            }
            catch (ClassifiedStoreException ex) when (ex.Kind == StoreErrorKind.Data && !ct.IsCancellationRequested)
            {
                if (batch.Count <= 1)
                {
                    if (budget[0] <= 0)
                        return new IsolationResult(EmptyOutcome, false, 0);   // 상한 초과 → 정지

                    budget[0]--;
                    _logger.LogError(ex, "{Queue} 데이터 오류 {SqlState} — 메시지 {MessageId} 1건 DLQ",
                        Queue, ex.SqlState, batch.Count == 1 ? batch[0].MessageId : "(빈 배치)");
                    var poison = batch.Count == 1 ? new[] { batch[0].DeliveryTag } : Array.Empty<ulong>();
                    return new IsolationResult(new BatchOutcome(poison, 0, 0, 0), true, batch.Count);
                }

                var mid = batch.Count / 2;
                var left = await TryIsolateDataAsync(batch.Take(mid).ToList(), budget, ct);
                if (!left.Ok) return new IsolationResult(EmptyOutcome, false, 0);
                var right = await TryIsolateDataAsync(batch.Skip(mid).ToList(), budget, ct);
                if (!right.Ok) return new IsolationResult(EmptyOutcome, false, 0);
                return new IsolationResult(Merge(left.Outcome, right.Outcome), true, left.DataDlq + right.DataDlq);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && !ct.IsCancellationRequested)
            {
                WorkerMetrics.DbRetry.Inc();
                _logger.LogWarning(ex, "{Queue} 분할 격리 중 일시 오류, {Delay}ms 후 재시도", Queue, backoff.TotalMilliseconds);
                backoff = await DelayBackoffAsync(backoff, ct);
            }
        }
    }

    // 현재 backoff만큼 기다리고 다음 backoff(×2, 상한 DbRetryMax)를 돌려준다
    private async Task<TimeSpan> DelayBackoffAsync(TimeSpan backoff, CancellationToken ct)
    {
        await Task.Delay(backoff, _time, ct);
        return TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, _options.DbRetryMax.Ticks));
    }

    // 정지 상태 게이지 · 경보. 전환될 때만 로그를 남긴다(재시도마다 Error를 쏟지 않게)
    private void SetHalted(bool halted, string? sqlState, Exception? ex)
    {
        if (halted && !_storeHalted)
            _logger.LogError(ex, "{Queue} 저장 정지 — 시스템 오류 {SqlState}. DB · 배포를 고치면 자동 재개", Queue, sqlState);
        else if (!halted && _storeHalted)
            _logger.LogInformation("{Queue} 저장 재개", Queue);

        _storeHalted = halted;
        WorkerMetrics.StoreHalted.WithLabels(Queue).Set(halted ? 1 : 0);
    }

    private readonly record struct IsolationResult(BatchOutcome Outcome, bool Ok, int DataDlq);

    private static readonly BatchOutcome EmptyOutcome = new(Array.Empty<ulong>(), 0, 0, 0);

    private static BatchOutcome Merge(BatchOutcome a, BatchOutcome b) => new(
        a.Poison.Concat(b.Poison).ToList(),
        a.Stored + b.Stored, a.Duplicate + b.Duplicate, a.Invalid + b.Invalid);
}
