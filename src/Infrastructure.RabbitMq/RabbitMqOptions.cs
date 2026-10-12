namespace RobotFleet.Infrastructure.RabbitMq;

/// 10 §3 · §4
public sealed class RabbitMqOptions
{
    /// AMQP URI. ConnectionStrings:RabbitMq
    public string ConnectionString { get; set; } = "";

    public string TopologyPrefix { get; set; } = "";

    public TimeSpan ReliableConfirmTimeout { get; set; } = TimeSpan.FromMilliseconds(2000);

    public TimeSpan TelemetryConfirmTimeout { get; set; } = TimeSpan.FromMilliseconds(200);

    /// 범주별 발행 채널 수 (M3 측정)
    public int PublishChannelsPerCategory { get; set; } = 2;

    public TimeSpan Heartbeat { get; set; } = TimeSpan.FromSeconds(10);
}
