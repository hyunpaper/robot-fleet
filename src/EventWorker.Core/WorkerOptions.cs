namespace RobotFleet.EventWorker;

/// 10 §6 · §7
public sealed class WorkerOptions
{
    /// 비우면 "{hostname}-{기동 unixMs}"
    public string WorkerId { get; set; } = "";
    public string TopologyPrefix { get; set; } = "";
    public ushort Prefetch { get; set; } = 50;
    public int BatchMax { get; set; } = 50;
    public TimeSpan BatchWindow { get; set; } = TimeSpan.FromMilliseconds(50);
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan RebalanceInterval { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan DbRetryMin { get; set; } = TimeSpan.FromMilliseconds(200);
    public TimeSpan DbRetryMax { get; set; } = TimeSpan.FromSeconds(5);
}
