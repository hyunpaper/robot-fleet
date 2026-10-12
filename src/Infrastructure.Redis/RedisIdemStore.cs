using Microsoft.Extensions.Logging;
using RobotFleet.Shared.Pipeline;

namespace RobotFleet.Infrastructure.Redis;

/// 08 §9 2단계 멱등 키. Redis 오류 · 타임아웃 = Bypass. 10 §4
public sealed class RedisIdemStore : IIdemStore
{
    private readonly RedisConnectionProvider _connections;
    private readonly RedisOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<RedisIdemStore> _logger;

    public RedisIdemStore(RedisConnectionProvider connections, RedisOptions options, TimeProvider time, ILogger<RedisIdemStore> logger)
    {
        _connections = connections;
        _options = options;
        _time = time;
        _logger = logger;
    }

    public Task<IdemBegin> TryBeginAsync(string batchId, CancellationToken ct) =>
        throw new NotImplementedException($"{_connections}{_options}{_time}{_logger}");

    public Task CompleteAsync(string batchId, CancellationToken ct) => throw new NotImplementedException();

    public Task AbortAsync(string batchId, long pendingSinceMs, CancellationToken ct) => throw new NotImplementedException();
}
