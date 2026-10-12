using System.Globalization;
using Microsoft.Extensions.Logging;
using RobotFleet.Shared.Pipeline;
using StackExchange.Redis;

namespace RobotFleet.Infrastructure.Redis;

/// 08 §9 2단계 멱등 키. Redis 오류 · 타임아웃 = Bypass(fail-open). 10 §4
public sealed class RedisIdemStore : IIdemStore
{
    private const int TtlSeconds = 600;
    private const int OrphanMs = 5000;
    private static readonly string BeginScript = RedisConnectionProvider.Script("idem_begin.lua");
    private static readonly string AbortScript = RedisConnectionProvider.Script("idem_abort.lua");

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

    public async Task<IdemBegin> TryBeginAsync(string batchId, CancellationToken ct)
    {
        try
        {
            var db = await _connections.TryGetDatabaseAsync();
            if (db is null) return new IdemBegin(IdemBeginStatus.Bypass, 0);
            var now = _time.GetUtcNow().ToUnixTimeMilliseconds();
            var result = (RedisResult[])(await db.ScriptEvaluateAsync(BeginScript, [Key(batchId)],
                [now, TtlSeconds, OrphanMs]).WaitAsync(_options.CommandTimeout, ct))!;
            var since = long.Parse((string)result[1]!, CultureInfo.InvariantCulture);
            return (string)result[0]! switch
            {
                "BEGIN" => new IdemBegin(IdemBeginStatus.Begin, since),
                "TAKEOVER" => new IdemBegin(IdemBeginStatus.Takeover, since),
                "DONE" => new IdemBegin(IdemBeginStatus.Done, 0),
                _ => new IdemBegin(IdemBeginStatus.Busy, since),
            };
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            _logger.LogWarning(ex, "멱등 키 조회 실패, 1단계 생략");
            return new IdemBegin(IdemBeginStatus.Bypass, 0);
        }
    }

    public async Task CompleteAsync(string batchId, CancellationToken ct)
    {
        try
        {
            var db = await _connections.TryGetDatabaseAsync();
            if (db is null) return;
            await db.StringSetAsync(Key(batchId), "done", TimeSpan.FromSeconds(TtlSeconds), When.Exists)
                .WaitAsync(_options.CommandTimeout, ct);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            _logger.LogWarning(ex, "멱등 키 done 기록 실패(무시)");
        }
    }

    public async Task AbortAsync(string batchId, long pendingSinceMs, CancellationToken ct)
    {
        try
        {
            var db = await _connections.TryGetDatabaseAsync();
            if (db is null) return;
            await db.ScriptEvaluateAsync(AbortScript, [Key(batchId)], [$"pending:{pendingSinceMs}"])
                .WaitAsync(_options.CommandTimeout, ct);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            _logger.LogWarning(ex, "멱등 키 삭제 실패(5초 뒤 고아로 재점유된다)");
        }
    }

    private RedisKey Key(string batchId) => _connections.Key("idem:" + batchId);
}
