using System.Threading.Channels;
using RobotFleet.Shared.Messaging;
using RobotFleet.Shared.Pipeline;

namespace RobotFleet.Simulator.Events;

/// 틱 루프 → 경계 있는 버퍼(IEventSink) → 백그라운드 순차 송신. 202 뒤에만 송신 로그. 10 §5
/// 머리 배치 하나만 보내고(peek), 202를 받아야 버퍼에서 꺼낸다 — 보내는 중인 배치도 버퍼 600에 포함된다
public sealed class EventBatchSender : IEventSink, IAsyncDisposable
{
    private readonly IIngestClient _client;
    private readonly SendLog? _log;
    private readonly EventSenderOptions _options;
    private readonly TimeProvider _time;
    private readonly Channel<EventBatch> _buffer;
    private readonly TaskCompletionSource _emptied = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile string? _fault;

    public EventBatchSender(IIngestClient client, SendLog? log, EventSenderOptions options, TimeProvider time)
    {
        _client = client;
        _log = log;
        _options = options;
        _time = time;
        _buffer = Channel.CreateBounded<EventBatch>(new BoundedChannelOptions(options.Capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });
    }

    /// 400을 받아 송신을 멈췄으면 true
    public bool Faulted => _fault is not null;

    public string? FaultReason => _fault;

    /// 202를 받은 배치 수
    public long Sent { get; private set; }

    public int Buffered => _buffer.Reader.Count;

    public bool TryWrite(EventBatch batch)
    {
        var ok = _buffer.Writer.TryWrite(batch);
        if (ok) SimulatorMetrics.BufferDepth.Set(_buffer.Reader.Count);
        return ok;
    }

    public ValueTask<bool> WaitToWriteAsync(CancellationToken ct) => _buffer.Writer.WaitToWriteAsync(ct);

    /// 송신 루프. 취소되거나, 입력이 닫히고 버퍼가 비거나, 400을 받으면 끝난다
    public async Task RunAsync(CancellationToken ct)
    {
        var reader = _buffer.Reader;
        try
        {
            while (await reader.WaitToReadAsync(ct))
            {
                if (!reader.TryPeek(out var batch)) continue;
                if (!await SendUntilAcceptedAsync(batch, ct)) return;
                reader.TryRead(out _);
                SimulatorMetrics.BufferDepth.Set(reader.Count);
            }

            _emptied.TrySetResult();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    /// 입력을 닫고 DrainTimeout까지 송신을 기다린다. 반환 = 못 보낸 배치 수(unsent)
    public async Task<int> DrainAsync(CancellationToken ct)
    {
        _buffer.Writer.TryComplete();
        if (!Faulted)
        {
            try
            {
                await _emptied.Task.WaitAsync(_options.DrainTimeout, _time, ct);
            }
            catch (TimeoutException)
            {
            }
        }

        return _buffer.Reader.Count;
    }

    public ValueTask DisposeAsync()
    {
        _buffer.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    // true = 202. false = 400(송신 정지)
    private async Task<bool> SendUntilAcceptedAsync(EventBatch batch, CancellationToken ct)
    {
        var body = FleetJson.Serialize(batch);   // 재시도는 같은 바이트
        var backoff = _options.BackoffMin;
        while (true)
        {
            IngestResult result;
            using (var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                attempt.CancelAfter(_options.AttemptTimeout);
                try
                {
                    result = await _client.SendAsync(batch, body, attempt.Token);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested && ex is OperationCanceledException or HttpRequestException)
                {
                    result = new IngestResult(IngestStatus.Unavailable, Error: ex.Message);
                }
            }

            switch (result.Status)
            {
                case IngestStatus.Accepted or IngestStatus.Duplicate:
                    _log?.Append(batch);
                    Sent++;
                    return true;
                case IngestStatus.Invalid:
                    Fail(batch, body, result.Error);
                    return false;
                default:
                    SimulatorMetrics.SendRetry.WithLabels("unavailable").Inc();
                    await Task.Delay(backoff, _time, ct);
                    backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, _options.BackoffMax.Ticks));
                    break;
            }
        }
    }

    // 검증 실패는 버그다. 재시도해도 같으므로 멈추고 본문을 남긴다(조용히 버리면 유실)
    private void Fail(EventBatch batch, byte[] body, string? error)
    {
        _fault = $"배치 {batch.BatchId}(tick {batch.Tick}) 400: {error}";
        if (_options.FailureDumpDirectory is { } dir)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, $"rejected-{batch.BatchId}.json"), body);
        }

        _buffer.Writer.TryComplete();   // 틱 루프의 WaitToWriteAsync가 false → 시뮬레이션 종료
    }
}
