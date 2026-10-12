// 10 §12 C-01 · C-02: 계약 기반 클래스를 실제 Redis에서. 접속 정보가 없으면 skip
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using RobotFleet.Infrastructure.Redis;
using RobotFleet.Shared.Pipeline;
using RobotFleet.TestSupport;
using RobotFleet.TestSupport.Contracts;
using StackExchange.Redis;

namespace RobotFleet.Infrastructure.Tests.Redis;

internal sealed class RedisRig : IAsyncDisposable
{
    public RedisRig()
    {
        Options = new RedisOptions { ConnectionString = TestEnv.Redis!, KeyPrefix = TestRun.New().RedisPrefix };
        Connections = new RedisConnectionProvider(Options, NullLogger<RedisConnectionProvider>.Instance);
    }

    public RedisOptions Options { get; }

    public RedisConnectionProvider Connections { get; }

    public async ValueTask DisposeAsync()
    {
        var mux = await ConnectionMultiplexer.ConnectAsync(TestEnv.Redis!);
        foreach (var server in mux.GetServers())
        {
            await foreach (var key in server.KeysAsync(pattern: Options.KeyPrefix + "*"))
                await mux.GetDatabase().KeyDeleteAsync(key);
        }

        await StaleResourceSweeper.SweepRedisAsync(mux, DateTimeOffset.UtcNow, default);
        await mux.DisposeAsync();
        await Connections.DisposeAsync();
    }
}

[Trait("Category", "Integration")]
public sealed class RedisIdemStoreContractTests : IdemStoreContract, IAsyncLifetime
{
    private RedisRig? _rig;

    protected override Task<IIdemStore> CreateAsync()
    {
        _rig ??= new RedisRig();
        return Task.FromResult<IIdemStore>(new RedisIdemStore(_rig.Connections, _rig.Options, Time, NullLogger<RedisIdemStore>.Instance));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _rig?.DisposeAsync().AsTask() ?? Task.CompletedTask;

    [IntegrationFact(TestEnv.RedisVar)] public Task C01_I01_신규_키는_BEGIN() => New_key_begins();
    [IntegrationFact(TestEnv.RedisVar)] public Task C01_I02_done_키는_DONE() => Completed_key_is_done();
    [IntegrationFact(TestEnv.RedisVar)] public Task C01_I03_5초_미만_pending은_BUSY() => Pending_under_5s_is_busy();
    [IntegrationFact(TestEnv.RedisVar)] public Task C01_I04_5초_이상_pending은_TAKEOVER() => Pending_over_5s_is_taken_over();
    [IntegrationFact(TestEnv.RedisVar)] public Task C01_I08_Abort는_내_pending만_지운다() => Abort_deletes_only_own_pending();
    [IntegrationFact(TestEnv.RedisVar)] public Task C01_Complete는_키가_있을_때만() => Complete_without_key_does_nothing();
}

[Trait("Category", "Integration")]
public sealed class RedisRosterContractTests : RosterContract, IAsyncLifetime
{
    private RedisRig? _rig;

    protected override Task<IPartitionRoster> CreateAsync()
    {
        _rig ??= new RedisRig();
        return Task.FromResult<IPartitionRoster>(new RedisPartitionRoster(_rig.Connections, _rig.Options, Time,
            NullLogger<RedisPartitionRoster>.Instance));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _rig?.DisposeAsync().AsTask() ?? Task.CompletedTask;

    [IntegrationFact(TestEnv.RedisVar)] public Task C02_R02_순위는_id_순() => Members_are_sorted_by_id_not_heartbeat();
    [IntegrationFact(TestEnv.RedisVar)] public Task C02_R06_30초_침묵은_제외() => Silent_member_expires_after_30s();
    [IntegrationFact(TestEnv.RedisVar)] public Task C02_탈퇴() => Leave_removes_member();
}

public sealed class RedisScriptTests
{
    [Theory]
    [InlineData("idem_begin.lua")]
    [InlineData("idem_abort.lua")]
    [InlineData("roster_heartbeat.lua")]
    public void Lua_스크립트가_어셈블리에_내장된다(string name) =>
        RedisConnectionProvider.Script(name).Should().Contain("KEYS[1]");

    [Fact]
    public async Task Redis_미설정이면_Bypass()
    {
        var store = new BypassIdemStore();
        (await store.TryBeginAsync("b", default)).Status.Should().Be(IdemBeginStatus.Bypass);
    }
}
