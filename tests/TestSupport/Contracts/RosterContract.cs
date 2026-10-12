using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using RobotFleet.Shared.Pipeline;

namespace RobotFleet.TestSupport.Contracts;

/// IPartitionRoster 계약 — R-01 · R-02 · R-06 중 명부 몫. 10 §7 · §12
public abstract class RosterContract
{
    protected FakeTimeProvider Time { get; } = new(DateTimeOffset.Parse("2026-10-12T00:00:00Z"));

    protected abstract Task<IPartitionRoster> CreateAsync();

    protected async Task Members_are_sorted_by_id_not_heartbeat()
    {
        var roster = await CreateAsync();
        await roster.HeartbeatAsync("w-b", default);
        Time.Advance(TimeSpan.FromSeconds(1));
        await roster.HeartbeatAsync("w-c", default);
        Time.Advance(TimeSpan.FromSeconds(1));
        var view = await roster.HeartbeatAsync("w-a", default);
        view!.Members.Should().Equal("w-a", "w-b", "w-c");
        Time.Advance(TimeSpan.FromSeconds(1));
        (await roster.HeartbeatAsync("w-b", default))!.RankOf("w-b").Should().Be(1);   // heartbeat로 순위가 바뀌지 않는다
    }

    protected async Task Silent_member_expires_after_30s()
    {
        var roster = await CreateAsync();
        await roster.HeartbeatAsync("w-old", default);
        Time.Advance(TimeSpan.FromSeconds(29));
        (await roster.HeartbeatAsync("w-new", default))!.Members.Should().Contain("w-old");
        Time.Advance(TimeSpan.FromSeconds(1));
        (await roster.HeartbeatAsync("w-new", default))!.Members.Should().Equal("w-new");
    }

    protected async Task Leave_removes_member()
    {
        var roster = await CreateAsync();
        await roster.HeartbeatAsync("w-1", default);
        await roster.HeartbeatAsync("w-2", default);
        await roster.LeaveAsync("w-1", default);
        (await roster.HeartbeatAsync("w-2", default))!.Members.Should().Equal("w-2");
    }
}
