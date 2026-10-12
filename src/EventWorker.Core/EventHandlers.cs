using Microsoft.Extensions.Logging;
using RobotFleet.Shared.Pipeline;

namespace RobotFleet.EventWorker;

/// 배치 하나를 StoreBatch로 바꾸고(전이 검증 포함) 저장한다. 10 §6
public sealed class EventHandlers
{
    private readonly IEventStore _store;
    private readonly IEventPublisher _publisher;
    private readonly WorkerOptions _options;
    private readonly ILogger<EventHandlers> _logger;

    public EventHandlers(IEventStore store, IEventPublisher publisher, WorkerOptions options, ILogger<EventHandlers> logger)
    {
        _store = store;
        _publisher = publisher;
        _options = options;
        _logger = logger;
    }

    /// 반환: 처리 결과(독 메시지 tag 목록 포함). 저장소 일시 오류는 예외로 던진다(호출자가 재시도)
    public Task<BatchOutcome> HandleAsync(IReadOnlyList<Delivery> batch, CancellationToken ct) =>
        throw new NotImplementedException($"{_store}{_publisher}{_options}{_logger}");
}

/// Poison = 역직렬화 실패 · 모르는 schemaVersion → Reject(DLX)
public sealed record BatchOutcome(IReadOnlyList<ulong> Poison, int Stored, int Duplicate, int Invalid);
