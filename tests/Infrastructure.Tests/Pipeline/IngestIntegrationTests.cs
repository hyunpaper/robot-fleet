// 10 §12 I 시리즈 중 실제 RabbitMQ · Redis로 검증하는 것. 검증 규칙(I-16)은 Pipeline.Tests의 순수 로직 테스트
using FluentAssertions;
using RobotFleet.Shared.Messaging;
using RobotFleet.Shared.Pipeline;
using RobotFleet.TestSupport;

namespace RobotFleet.Infrastructure.Tests.Pipeline;

[Trait("Category", "Integration")]
public sealed class IngestIntegrationTests : PipelineTestBase
{
    [IntegrationFact(TestEnv.RabbitMqVar, TestEnv.RedisVar)]
    public async Task I01_I02_신규는_발행하고_done_재전송은_202_중복()
    {
        var rig = await RigAsync(redis: true, postgres: false);
        var batch = Batch(1, Created("T-1"), Telemetry("R-1"));
        var first = await rig.Ingest.HandleAsync(FleetJson.Serialize(batch), default);
        first.StatusCode.Should().Be(202);
        await PipelineRig.UntilAsync(async () => await rig.ReadyAsync() == 2);

        var again = await rig.Ingest.HandleAsync(FleetJson.Serialize(batch), default);
        again.StatusCode.Should().Be(202);
        again.Body.Duplicate.Should().BeTrue();
        (await rig.Idem!.TryBeginAsync(batch.BatchId, default)).Status.Should().Be(IdemBeginStatus.Done);
        await Task.Delay(300);
        (await rig.ReadyAsync()).Should().Be(2);
    }

    [IntegrationFact(TestEnv.RabbitMqVar, TestEnv.RedisVar)]
    public async Task I03_다른_인스턴스가_발행_중이면_503()
    {
        var rig = await RigAsync(redis: true, postgres: false);
        var batch = Batch(1, Created("T-1"));
        await rig.Idem!.TryBeginAsync(batch.BatchId, default);
        var response = await rig.Ingest.HandleAsync(FleetJson.Serialize(batch), default);
        response.StatusCode.Should().Be(503);
        response.RetryAfterSeconds.Should().Be(1);
        (await rig.ReadyAsync()).Should().Be(0);
    }

    [IntegrationFact(TestEnv.RabbitMqVar)]
    public async Task I07_Redis_미설정이면_1단계를_건너뛰고_202()
    {
        var rig = await RigAsync(redis: false, postgres: false);
        var batch = Batch(1, Created("T-1"));
        (await rig.Ingest.HandleAsync(FleetJson.Serialize(batch), default)).StatusCode.Should().Be(202);
        (await rig.Ingest.HandleAsync(FleetJson.Serialize(batch), default)).StatusCode.Should().Be(202);   // 중복은 워커 몫
        await PipelineRig.UntilAsync(async () => await rig.ReadyAsync() == 2);
    }

    [IntegrationFact(TestEnv.RabbitMqVar, TestEnv.RedisVar)]
    public async Task I15_같은_로봇의_이벤트는_배열_순서대로_큐에()
    {
        var rig = await RigAsync(redis: true, postgres: false);
        var batch = Batch(1, Status("R-1", 0, 1, "task_assigned"), Telemetry("R-1"), Status("R-1", 1, 2, "conflict_wait"));
        await rig.SendAsync(batch);

        var queue = Enumerable.Range(0, TopologySpec.Partitions).Select(p => TopologySpec.QueueName(EventCategory.Task, p, rig.Rabbit.Prefix))
            .First(q => rig.CountAsync(q).Result > 0);
        var got = new System.Collections.Concurrent.ConcurrentQueue<Delivery>();
        await using var sub = await rig.Rabbit.Subscriber.SubscribeAsync(queue, 10, (d, _) => { got.Enqueue(d); return ValueTask.CompletedTask; }, default);
        await PipelineRig.UntilAsync(() => Task.FromResult(got.Count == 2));
        got.Select(d => d.MessageId).Should().Equal($"{batch.BatchId}/0", $"{batch.BatchId}/2");
        FleetJson.Deserialize<EventEnvelope>(got.First().Body.Span).TickMs.Should().Be(100);
    }
}
