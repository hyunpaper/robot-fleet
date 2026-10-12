namespace RobotFleet.Infrastructure.Redis;

/// 10 §4 · §7
public sealed class RedisOptions
{
    /// StackExchange.Redis 구성 문자열. ConnectionStrings:Redis. 비면 fail-open
    public string ConnectionString { get; set; } = "";

    /// 키 접두사(테스트 격리)
    public string KeyPrefix { get; set; } = "";

    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromMilliseconds(200);

    public TimeSpan ReconnectInterval { get; set; } = TimeSpan.FromSeconds(1);
}
