// 10 §12 W 시리즈 중 실제 RabbitMQ · PostgreSQL로 검증하는 것
using System.Text;
using FluentAssertions;
using Npgsql;
using RobotFleet.Shared.Messaging;
using RobotFleet.Shared.Pipeline;
using RobotFleet.TestSupport;

namespace RobotFleet.Infrastructure.Tests.Pipeline;

[Trait("Category", "Integration")]
public sealed class WorkerIntegrationTests : PipelineTestBase
{
    [IntegrationFact(TestEnv.RabbitMqVar, TestEnv.PgVar)]
    public async Task W02_ack는_커밋_뒤에만_커밋_전_kill이면_전부_반환()
    {
        var rig = await RigAsync(redis: false, postgres: true);
        await rig.SendAsync(Batch(1, Created("T-1"), Created("T-1"), Created("T-1")));

        // 영수증 테이블을 잠가 저장(커밋)을 멈춘다
        await using var locker = await rig.Pg!.DataSource.OpenConnectionAsync();
        await using var tx = await locker.BeginTransactionAsync();
        await using (var lockCmd = new NpgsqlCommand("LOCK TABLE task_event_log IN ACCESS EXCLUSIVE MODE", locker, tx))
            await lockCmd.ExecuteNonQueryAsync();

        var a = rig.StartWorker("w-a");
        await PipelineRig.UntilAsync(async () => await rig.ReadyAsync() == 0);   // 전달됨(미확인)
        await Task.Delay(1000);
        await rig.KillAsync(a);                                                   // 커밋 전 사망
        await PipelineRig.UntilAsync(async () => await rig.ReadyAsync() == 3);    // ack된 것이 없으니 3건 전부 반환
        await tx.RollbackAsync();

        rig.StartWorker("w-b");
        await PipelineRig.UntilAsync(async () => await rig.ScalarAsync("SELECT count(*) FROM task_event_log") == 3);
        await rig.DrainedAsync();
    }

    [IntegrationFact(TestEnv.RabbitMqVar, TestEnv.PgVar)]
    public async Task W05_처리_중_kill_뒤_재처리해도_행은_1회()
    {
        var rig = await RigAsync(redis: false, postgres: true);
        for (var t = 0; t < 10; t++)
            await rig.SendAsync(Batch(t, Enumerable.Range(0, 20).Select(i => Created($"T-{t:D2}-{i:D2}")).ToArray()));

        var a = rig.StartWorker("w-a");
        await PipelineRig.UntilAsync(async () => await rig.ScalarAsync("SELECT count(*) FROM task_event_log") > 0);
        await rig.KillAsync(a);
        rig.StartWorker("w-b");

        await PipelineRig.UntilAsync(async () => await rig.ScalarAsync("SELECT count(*) FROM task_event_log") == 200);
        await rig.DrainedAsync();
        (await rig.ScalarAsync("SELECT count(*) FROM task_history")).Should().Be(200);
    }

    [IntegrationFact(TestEnv.RabbitMqVar, TestEnv.PgVar)]
    public async Task W06_역직렬화_실패는_그_건만_DLQ()
    {
        var rig = await RigAsync(redis: false, postgres: true);
        await rig.SendAsync(Batch(1, Created("T-1")));
        await rig.Rabbit.Publisher.PublishBatchAsync(
            [new OutgoingMessage(EventCategory.Task, "T-1", "bad/0", EventType.TaskCreated, true, Encoding.UTF8.GetBytes("{oops"))], default);
        await rig.SendAsync(Batch(2, Created("T-1")));

        rig.StartWorker("w-a");
        await PipelineRig.UntilAsync(async () => await rig.CountAsync(TopologySpec.DlqName(rig.Rabbit.Prefix)) == 1);
        await PipelineRig.UntilAsync(async () => await rig.ScalarAsync("SELECT count(*) FROM task_event_log") == 2);
    }

    [IntegrationFact(TestEnv.RabbitMqVar, TestEnv.PgVar)]
    public async Task W08_금지_전이는_valid_false_영수증_상태_불변_invalid_발행()
    {
        var rig = await RigAsync(redis: false, postgres: true);
        await rig.SendAsync(Batch(1, Status("R-1", 0, 1, "task_assigned")));
        await rig.SendAsync(Batch(2, Status("R-1", 1, 4, "unload_done")));

        rig.StartWorker("w-a");
        await PipelineRig.UntilAsync(async () => await rig.ScalarAsync("SELECT count(*) FROM robot_status_log") == 2);
        (await rig.ScalarAsync("SELECT count(*) FROM robot_status_log WHERE NOT valid")).Should().Be(1);
        (await rig.ScalarAsync("SELECT status FROM robot_state WHERE robot_id = 'R-1'")).Should().Be(1);
        await PipelineRig.UntilAsync(async () => await rig.CountAsync(TopologySpec.InvalidQueueName(rig.Rabbit.Prefix)) == 1);
    }

    [IntegrationFact(TestEnv.RabbitMqVar, TestEnv.PgVar)]
    public async Task R_run_01_새_run의_첫_전이는_이전_run_상태와_비교하지_않는다()
    {
        var rig = await RigAsync(redis: false, postgres: true);
        const string runB = "20261013-150000-r200";
        await rig.SendAsync(Batch(1, Status("R-001", 0, 1, "task_assigned")));      // run A: Idle → Moving
        await rig.SendAsync(BatchOf(runB, 1, Status("R-001", 0, 1, "task_assigned")));   // run B: 다시 Idle에서 시작

        rig.StartWorker("w-a");
        await PipelineRig.UntilAsync(async () => await rig.ScalarAsync("SELECT count(*) FROM robot_status_log") == 2);
        await rig.DrainedAsync();
        (await rig.ScalarAsync("SELECT count(*) FROM robot_status_log WHERE NOT valid")).Should().Be(0);
        (await rig.ScalarAsync("SELECT count(*) FROM robot_state WHERE robot_id = 'R-001'")).Should().Be(2);
        (await rig.CountAsync(TopologySpec.InvalidQueueName(rig.Rabbit.Prefix))).Should().Be(0);
    }

    [IntegrationFact(TestEnv.RabbitMqVar, TestEnv.PgVar)]
    public async Task W09_재전달된_상태_변경은_오탐하지_않는다()
    {
        var rig = await RigAsync(redis: false, postgres: true);
        var batch = Batch(1, Status("R-1", 0, 1, "task_assigned"));
        await rig.SendAsync(batch);
        await rig.SendAsync(batch);   // Redis 미설정 → Ingest를 통과해 같은 eventId가 두 번 큐에

        rig.StartWorker("w-a");
        await PipelineRig.UntilAsync(async () => await rig.ScalarAsync("SELECT count(*) FROM robot_status_log") == 1);
        await rig.DrainedAsync();
        (await rig.ScalarAsync("SELECT count(*) FROM robot_status_log WHERE NOT valid")).Should().Be(0);
        (await rig.CountAsync(TopologySpec.InvalidQueueName(rig.Rabbit.Prefix))).Should().Be(0);
    }
}
