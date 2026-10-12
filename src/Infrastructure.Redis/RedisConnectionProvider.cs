using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace RobotFleet.Infrastructure.Redis;

/// 연결 실패를 던지지 않는다. 불통이면 null, 백그라운드에서 ReconnectInterval마다 재시도
public sealed class RedisConnectionProvider : IAsyncDisposable
{
    private readonly RedisOptions _options;
    private readonly ILogger<RedisConnectionProvider> _logger;

    public RedisConnectionProvider(RedisOptions options, ILogger<RedisConnectionProvider> logger)
    {
        _options = options;
        _logger = logger;
    }

    public IDatabase? TryGetDatabase() => throw new NotImplementedException($"{_options}{_logger}");

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
