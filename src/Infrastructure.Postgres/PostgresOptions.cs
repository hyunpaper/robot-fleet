namespace RobotFleet.Infrastructure.Postgres;

/// 10 §8
public sealed class PostgresOptions
{
    /// Npgsql 연결 문자열. ConnectionStrings:Postgres
    public string ConnectionString { get; set; } = "";

    /// 워커 최대 8대 × 10 = 80 < max_connections 100
    public int MaxPoolSize { get; set; } = 10;

    public int CommandTimeoutSeconds { get; set; } = 10;
}
