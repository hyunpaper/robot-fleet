using RobotFleet.Shared.Messaging;
using RobotFleet.Shared.Pipeline;

namespace RobotFleet.Simulator.Events;

/// 틱 루프 → 경계 있는 버퍼(IEventSink) → 백그라운드 순차 송신. 202 뒤에만 송신 로그. 10 §5
public sealed class EventBatchSender : IEventSink, IAsyncDisposable
{
    private readonly IIngestClient _client;
    private readonly SendLog? _log;
    private readonly EventSenderOptions _options;
    private readonly TimeProvider _time;

    public EventBatchSender(IIngestClient client, SendLog? log, EventSenderOptions options, TimeProvider time)
    {
        _client = client;
        _log = log;
        _options = options;
        _time = time;
    }

    /// 400을 받아 송신을 멈췄으면 true
    public bool Faulted => throw new NotImplementedException($"{_client}{_log}{_options}{_time}");

    public string? FaultReason => throw new NotImplementedException();

    public bool TryWrite(EventBatch batch) => throw new NotImplementedException();

    public ValueTask<bool> WaitToWriteAsync(CancellationToken ct) => throw new NotImplementedException();

    /// 송신 루프. 취소되면 끝난다(버퍼 비우기는 DrainAsync)
    public Task RunAsync(CancellationToken ct) => throw new NotImplementedException();

    /// 입력을 닫고 DrainTimeout까지 송신을 기다린다. 반환 = 못 보낸 배치 수(unsent)
    public Task<int> DrainAsync(CancellationToken ct) => throw new NotImplementedException();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
