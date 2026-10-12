using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace RobotFleet.Infrastructure.RabbitMq;

/// 서비스당 연결 1개. 첫 사용 시 연결, 자동 복구 켬 · 토폴로지 복구 끔. 10 §3
public sealed class RabbitConnectionProvider : IAsyncDisposable
{
    private readonly RabbitMqOptions _options;
    private readonly ILogger<RabbitConnectionProvider> _logger;

    public RabbitConnectionProvider(RabbitMqOptions options, ILogger<RabbitConnectionProvider> logger)
    {
        _options = options;
        _logger = logger;
    }

    public bool IsOpen => throw new NotImplementedException($"{_options}{_logger}");

    public Task<IConnection> GetAsync(CancellationToken ct) => throw new NotImplementedException();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
