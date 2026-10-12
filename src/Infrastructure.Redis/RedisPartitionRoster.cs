using Microsoft.Extensions.Logging;
using RobotFleet.Shared.Pipeline;

namespace RobotFleet.Infrastructure.Redis;

/// worker:members Sorted Set, heartbeat 점수, 30초 만료. 10 §7
public sealed class RedisPartitionRoster : IPartitionRoster
{
    private readonly RedisConnectionProvider _connections;
    private readonly RedisOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<RedisPartitionRoster> _logger;

    public RedisPartitionRoster(RedisConnectionProvider connections, RedisOptions options, TimeProvider time, ILogger<RedisPartitionRoster> logger)
    {
        _connections = connections;
        _options = options;
        _time = time;
        _logger = logger;
    }

    public bool IsConfigured => true;

    public Task<RosterView?> HeartbeatAsync(string workerId, CancellationToken ct) =>
        throw new NotImplementedException($"{_connections}{_options}{_time}{_logger}");

    public Task LeaveAsync(string workerId, CancellationToken ct) => throw new NotImplementedException();
}
