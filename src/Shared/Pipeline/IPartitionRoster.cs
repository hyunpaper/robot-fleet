namespace RobotFleet.Shared.Pipeline;

/// Members는 id 문자열 오름차순
public sealed record RosterView(IReadOnlyList<string> Members)
{
    /// 없으면 -1
    public int RankOf(string workerId)
    {
        for (var i = 0; i < Members.Count; i++)
        {
            if (Members[i] == workerId)
            {
                return i;
            }
        }

        return -1;
    }
}

public interface IPartitionRoster
{
    /// null = 불통
    Task<RosterView?> HeartbeatAsync(string workerId, CancellationToken ct);

    Task LeaveAsync(string workerId, CancellationToken ct);
}
