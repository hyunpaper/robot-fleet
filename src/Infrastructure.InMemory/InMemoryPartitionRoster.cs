using RobotFleet.Shared.Pipeline;

namespace RobotFleet.Infrastructure.InMemory;

/// 만료 30s. RosterView는 살아 있는 id 오름차순
public sealed class InMemoryPartitionRoster(TimeProvider time, bool configured = true) : IPartitionRoster
{
    private const long ExpireMs = 30_000;

    private readonly object gate = new();
    private readonly Dictionary<string, long> lastBeat = [];
    private bool connected = true;

    public bool IsConfigured => configured;

    /// 이후 HeartbeatAsync = null
    public void Disconnect()
    {
        lock (gate)
        {
            connected = false;
        }
    }

    public void Reconnect()
    {
        lock (gate)
        {
            connected = true;
        }
    }

    public Task<RosterView?> HeartbeatAsync(string workerId, CancellationToken ct)
    {
        lock (gate)
        {
            if (!connected)
            {
                return Task.FromResult<RosterView?>(null);
            }

            var now = time.GetUtcNow().ToUnixTimeMilliseconds();
            lastBeat[workerId] = now;
            foreach (var expired in lastBeat.Where(p => now - p.Value >= ExpireMs).Select(p => p.Key).ToList())
            {
                lastBeat.Remove(expired);
            }

            var members = lastBeat.Keys.Order(StringComparer.Ordinal).ToList();
            return Task.FromResult<RosterView?>(new RosterView(members));
        }
    }

    public Task LeaveAsync(string workerId, CancellationToken ct)
    {
        lock (gate)
        {
            if (connected)
            {
                lastBeat.Remove(workerId);
            }
        }

        return Task.CompletedTask;
    }
}
