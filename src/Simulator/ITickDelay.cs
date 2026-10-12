namespace RobotFleet.Simulator;

public interface ITickDelay { Task DelayAsync(int ms, CancellationToken ct); }

public sealed class RealTimeDelay : ITickDelay
{
    public Task DelayAsync(int ms, CancellationToken ct) => Task.Delay(ms, ct);
}

/// <summary>대기 없음. 테스트·--speed 0.</summary>
public sealed class NoDelay : ITickDelay
{
    public Task DelayAsync(int ms, CancellationToken ct) => Task.CompletedTask;
}
