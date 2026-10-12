namespace RobotFleet.Simulator.Events;

/// 10 §5
public sealed record EventSenderOptions
{
    public int Capacity { get; init; } = 600;

    public TimeSpan AttemptTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan BackoffMin { get; init; } = TimeSpan.FromMilliseconds(100);

    public TimeSpan BackoffMax { get; init; } = TimeSpan.FromSeconds(2);

    /// 종료 시 버퍼를 비우는 최대 대기
    public TimeSpan DrainTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// 400 응답 시 배치 덤프 위치(null이면 덤프 안 함)
    public string? FailureDumpDirectory { get; init; }
}
