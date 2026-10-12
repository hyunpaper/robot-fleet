using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace RobotFleet.Infrastructure.RabbitMq;

/// 서비스당 연결 1개. 첫 사용 시 연결, 자동 복구 켬 · 토폴로지 복구 끔(재선언 · 재구독은 우리가 한다). 10 §3
public sealed class RabbitConnectionProvider : IAsyncDisposable
{
    private readonly RabbitMqOptions _options;
    private readonly ILogger<RabbitConnectionProvider> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;

    public RabbitConnectionProvider(RabbitMqOptions options, ILogger<RabbitConnectionProvider> logger)
    {
        _options = options;
        _logger = logger;
    }

    public bool IsOpen => _connection?.IsOpen ?? false;

    public async Task<IConnection> GetAsync(CancellationToken ct)
    {
        if (_connection is { IsOpen: true } open) return open;
        await _gate.WaitAsync(ct);
        try
        {
            if (_connection is { IsOpen: true } again) return again;
            if (_connection is not null) await _connection.DisposeAsync();
            var factory = new ConnectionFactory
            {
                Uri = new Uri(_options.ConnectionString),
                AutomaticRecoveryEnabled = true,
                TopologyRecoveryEnabled = false,
                RequestedHeartbeat = _options.Heartbeat,
                ClientProvidedName = "robot-fleet",
            };
            _connection = await factory.CreateConnectionAsync(ct);
            _logger.LogInformation("RabbitMQ 연결 {Host}:{Port} vhost {VHost}", factory.HostName, factory.Port, factory.VirtualHost);
            return _connection;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            try
            {
                await _connection.CloseAsync();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "연결 닫기 실패");
            }

            await _connection.DisposeAsync();
        }
    }
}
