using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using RobotFleet.Shared.Pipeline;

namespace RobotFleet.Infrastructure.Tests.Contracts;

/// IIdemStore 계약 — 08 §9 상태 전이 표 (I-01~I-08 중 저장소 몫). 실제 Redis에서 돈다. 10 §12
public abstract class IdemStoreContract
{
    protected FakeTimeProvider Time { get; } = new(DateTimeOffset.Parse("2026-10-12T00:00:00Z"));

    protected abstract Task<IIdemStore> CreateAsync();

    protected async Task New_key_begins()
    {
        var store = await CreateAsync();
        var begin = await store.TryBeginAsync("b-new", default);
        begin.Status.Should().Be(IdemBeginStatus.Begin);
        begin.PendingSinceMs.Should().Be(Time.GetUtcNow().ToUnixTimeMilliseconds());
    }

    protected async Task Completed_key_is_done()
    {
        var store = await CreateAsync();
        await store.TryBeginAsync("b-done", default);
        await store.CompleteAsync("b-done", default);
        (await store.TryBeginAsync("b-done", default)).Status.Should().Be(IdemBeginStatus.Done);
    }

    protected async Task Pending_under_5s_is_busy()
    {
        var store = await CreateAsync();
        await store.TryBeginAsync("b-busy", default);
        Time.Advance(TimeSpan.FromMilliseconds(4999));
        (await store.TryBeginAsync("b-busy", default)).Status.Should().Be(IdemBeginStatus.Busy);
    }

    protected async Task Pending_over_5s_is_taken_over()
    {
        var store = await CreateAsync();
        await store.TryBeginAsync("b-orphan", default);
        Time.Advance(TimeSpan.FromMilliseconds(5000));
        var takeover = await store.TryBeginAsync("b-orphan", default);
        takeover.Status.Should().Be(IdemBeginStatus.Takeover);
        takeover.PendingSinceMs.Should().Be(Time.GetUtcNow().ToUnixTimeMilliseconds());
    }

    protected async Task Abort_deletes_only_own_pending()
    {
        var store = await CreateAsync();
        var first = await store.TryBeginAsync("b-abort", default);
        Time.Advance(TimeSpan.FromSeconds(6));
        await store.TryBeginAsync("b-abort", default);              // 재점유
        await store.AbortAsync("b-abort", first.PendingSinceMs, default);   // 원래 인스턴스의 늦은 Abort
        (await store.TryBeginAsync("b-abort", default)).Status.Should().Be(IdemBeginStatus.Busy);

        var own = await store.TryBeginAsync("b-abort2", default);
        await store.AbortAsync("b-abort2", own.PendingSinceMs, default);
        (await store.TryBeginAsync("b-abort2", default)).Status.Should().Be(IdemBeginStatus.Begin);
    }

    protected async Task Complete_without_key_does_nothing()
    {
        var store = await CreateAsync();
        await store.CompleteAsync("b-none", default);   // SET XX
        (await store.TryBeginAsync("b-none", default)).Status.Should().Be(IdemBeginStatus.Begin);
    }
}
