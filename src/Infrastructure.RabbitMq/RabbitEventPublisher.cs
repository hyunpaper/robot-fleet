using Microsoft.Extensions.Logging;
using RobotFleet.Shared.Pipeline;

namespace RobotFleet.Infrastructure.RabbitMq;

/// 배열 순서대로 직렬 송신, 확인은 시퀀스 번호로 몰아 받는다. 범주별 타임아웃. 10 §4
public sealed class RabbitEventPublisher : IEventPublisher, IAsyncDisposable
{
    private readonly RabbitConnectionProvider _connections;
    private readonly RabbitMqOptions _options;
    private readonly ILogger<RabbitEventPublisher> _logger;

    public RabbitEventPublisher(RabbitConnectionProvider connections, RabbitMqOptions options, ILogger<RabbitEventPublisher> logger)
    {
        _connections = connections;
        _options = options;
        _logger = logger;
    }

    public Task<PublishOutcome> PublishBatchAsync(IReadOnlyList<OutgoingMessage> messages, CancellationToken ct) =>
        throw new NotImplementedException($"{_connections}{_options}{_logger}");

    public Task PublishToQueueAsync(string queue, OutgoingMessage message, CancellationToken ct) =>
        throw new NotImplementedException();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
