using Microsoft.Extensions.Logging;
using RobotFleet.Shared.Pipeline;
using StackExchange.Redis;

namespace RobotFleet.Infrastructure.Redis;

/// worker:members Sorted Set, 점수 = heartbeat ms, 30초 만료, 순위 = id 문자열 순. 10 §7
public sealed class RedisPartitionRoster : IPartitionRoster
{
    private const long ExpireMs = 30_000;
    private static readonly string HeartbeatScript = RedisConnectionProvider.Script("roster_heartbeat.lua");

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

    public async Task<RosterView?> HeartbeatAsync(string workerId, CancellationToken ct)
    {
        try
        {
            var db = await _connections.TryGetDatabaseAsync();
            if (db is null) return null;
            var now = _time.GetUtcNow().ToUnixTimeMilliseconds();
            var members = (RedisResult[])(await db.ScriptEvaluateAsync(HeartbeatScript, [Key], [workerId, now, ExpireMs])
                .WaitAsync(_options.CommandTimeout, ct))!;
            return new RosterView(members.Select(m => (string)m!).Order(StringComparer.Ordinal).ToList());
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            _logger.LogWarning(ex, "명부 heartbeat 실패");
            return null;
        }
    }

    public async Task LeaveAsync(string workerId, CancellationToken ct)
    {
        try
        {
            var db = await _connections.TryGetDatabaseAsync();
            if (db is not null)
                await db.SortedSetRemoveAsync(Key, workerId).WaitAsync(_options.CommandTimeout, ct);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            _logger.LogWarning(ex, "명부 탈퇴 실패(30초 뒤 만료)");
        }
    }

    private RedisKey Key => _connections.Key("worker:members");
}
