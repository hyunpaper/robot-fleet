using Microsoft.Extensions.Logging;
using RobotFleet.Shared.Pipeline;

namespace RobotFleet.Ingest;

/// 검증 → 멱등 → 발행 → 응답. 10 §4 · 08 §9
public sealed class IngestService
{
    private readonly IIdemStore _idem;
    private readonly IEventPublisher _publisher;
    private readonly TimeProvider _time;
    private readonly ILogger<IngestService> _logger;

    public IngestService(IIdemStore idem, IEventPublisher publisher, TimeProvider time, ILogger<IngestService> logger)
    {
        _idem = idem;
        _publisher = publisher;
        _time = time;
        _logger = logger;
    }

    /// body = POST /api/v1/events/batch 요청 본문(UTF-8 JSON)
    public Task<IngestResponse> HandleAsync(ReadOnlyMemory<byte> body, CancellationToken ct) =>
        throw new NotImplementedException($"{nameof(IngestService)} {_idem}{_publisher}{_time}{_logger}");
}
