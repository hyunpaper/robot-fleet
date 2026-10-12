// 10 §12 R 시리즈 중 실제 Redis 명부 · RabbitMQ · PostgreSQL로 검증하는 것
using FluentAssertions;
using RobotFleet.TestSupport;

namespace RobotFleet.Infrastructure.Tests.Pipeline;

[Trait("Category", "Integration")]
public sealed class AssignmentIntegrationTests : PipelineTestBase
{
    [IntegrationFact(TestEnv.RabbitMqVar, TestEnv.RedisVar, TestEnv.PgVar)]
    public async Task R01_R05_몫은_p_mod_N_eq_rank_두_번_연속일_때_적용()
    {
        var rig = await RigAsync(redis: true, postgres: true);
        var a = rig.Worker("w-a", useRoster: true);
        var b = rig.Worker("w-b", useRoster: true);
        await a.RebalanceOnceAsync(default);
        a.OwnedPartitions.Should().BeEmpty();   // 한 번으로는 적용하지 않는다
        await b.RebalanceOnceAsync(default);
        for (var i = 0; i < 2; i++)
        {
            await a.RebalanceOnceAsync(default);
            await b.RebalanceOnceAsync(default);
        }

        a.OwnedPartitions.Should().Equal(0, 2, 4, 6, 8, 10, 12, 14);
        b.OwnedPartitions.Should().Equal(1, 3, 5, 7, 9, 11, 13, 15);
        await a.KillAsync();
        await b.KillAsync();
    }

    [IntegrationFact(TestEnv.RabbitMqVar, TestEnv.PgVar)]
    public async Task R07_명부가_없으면_단일_워커가_전부()
    {
        var rig = await RigAsync(redis: false, postgres: true);
        var w = rig.Worker("w-solo");
        await w.RebalanceOnceAsync(default);
        w.OwnedPartitions.Should().HaveCount(16);
        await w.KillAsync();
    }

    [IntegrationFact(TestEnv.RabbitMqVar, TestEnv.RedisVar, TestEnv.PgVar)]
    public async Task R04_합류_탈퇴_재배정을_거쳐도_로봇별_순서_유지_invalid_0()
    {
        var rig = await RigAsync(redis: true, postgres: true);
        (int From, int To, string Reason)[] cycle =
        [
            (0, 1, "task_assigned"), (1, 2, "conflict_wait"), (2, 1, "path_cleared"), (1, 3, "arrived_origin"),
            (3, 1, "load_done"), (1, 4, "arrived_destination"), (4, 0, "unload_done"),
        ];
        var robots = Enumerable.Range(0, 40).Select(i => $"R-{i:D2}").ToArray();
        rig.StartWorker("w-a", useRoster: true);
        RobotFleet.EventWorker.AssignmentCoordinator? b = null;
        const int steps = 140;
        for (var step = 0; step < steps; step++)
        {
            var (from, to, reason) = cycle[step % cycle.Length];
            await rig.SendAsync(Batch(step, robots.Select(r => Status(r, from, to, reason)).ToArray()));
            if (step == 30) b = rig.StartWorker("w-b", useRoster: true);   // 합류 → 재배정
            if (step == 90) await rig.StopAsync(b!);                          // 탈퇴 → 재배정
            if (step % 10 == 0) await Task.Delay(300);
        }

        await PipelineRig.UntilAsync(async () => await rig.ScalarAsync("SELECT count(*) FROM robot_status_log") == steps * robots.Length,
            timeoutMs: 120_000);
        (await rig.ScalarAsync("SELECT count(*) FROM robot_status_log WHERE NOT valid")).Should().Be(0);
        (await rig.ScalarAsync($"SELECT count(*) FROM robot_state WHERE status = {cycle[(steps - 1) % cycle.Length].To}"))
            .Should().Be(robots.Length);
    }
}
