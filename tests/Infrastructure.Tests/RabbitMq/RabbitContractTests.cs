// 10 §12 C-04 · T-01~T-03: 실제 RabbitMQ. 접두사로 자원을 나누고 끝나면 지운다. 접속 정보가 없으면 skip
using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;
using RobotFleet.Infrastructure.RabbitMq;
using RobotFleet.Shared.Messaging;
using RobotFleet.Shared.Pipeline;
using RobotFleet.TestSupport;
using RobotFleet.TestSupport.Contracts;

namespace RobotFleet.Infrastructure.Tests.RabbitMq;

internal sealed class RabbitRig : IAsyncDisposable
{
    public RabbitRig()
    {
        Options = new RabbitMqOptions { ConnectionString = TestEnv.RabbitMq!, TopologyPrefix = TestRun.New().RabbitPrefix };
        Connections = new RabbitConnectionProvider(Options, NullLogger<RabbitConnectionProvider>.Instance);
        Declarer = new TopologyDeclarer(Connections, Options, NullLogger<TopologyDeclarer>.Instance);
        Publisher = new RabbitEventPublisher(Connections, Options, NullLogger<RabbitEventPublisher>.Instance);
        Subscriber = new RabbitPartitionSubscriber(Connections, NullLogger<RabbitPartitionSubscriber>.Instance);
    }

    public RabbitMqOptions Options { get; }

    public RabbitConnectionProvider Connections { get; }

    public TopologyDeclarer Declarer { get; }

    public RabbitEventPublisher Publisher { get; }

    public RabbitPartitionSubscriber Subscriber { get; }

    public string Prefix => Options.TopologyPrefix;

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Declarer.DeleteAsync(default);
            await StaleResourceSweeper.SweepRabbitAsync(TestEnv.RabbitMgmt, DateTimeOffset.UtcNow, default);
        }
        finally
        {
            await Publisher.DisposeAsync();
            await Connections.DisposeAsync();
        }
    }
}

[Trait("Category", "Integration")]
public sealed class RabbitBrokerContractTests : BrokerContract, IAsyncLifetime
{
    private RabbitRig? _rig;

    protected override async Task<(IEventPublisher Publisher, IPartitionSubscriber Subscriber, string Prefix)> CreateAsync()
    {
        _rig ??= new RabbitRig();
        await _rig.Declarer.RunAsync(default);
        return (_rig.Publisher, _rig.Subscriber, _rig.Prefix);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _rig?.DisposeAsync().AsTask() ?? Task.CompletedTask;

    [IntegrationFact(TestEnv.RabbitMqVar)] public Task C04_같은_키는_한_큐에_순서대로() => Same_key_lands_in_one_queue_in_order();
    [IntegrationFact(TestEnv.RabbitMqVar)] public Task C04_prefetch와_multiple_ack() => Prefetch_limits_unacked_and_multiple_ack_releases();
    [IntegrationFact(TestEnv.RabbitMqVar)] public Task C04_SAC_채널_닫기_반환() => Single_active_consumer_hands_over_unacked_on_close();
    [IntegrationFact(TestEnv.RabbitMqVar)] public Task C04_reject는_DLQ() => Reject_goes_to_dlq();
    [IntegrationFact(TestEnv.RabbitMqVar)] public Task C04_invalid_큐_발행() => Publish_to_invalid_queue_is_confirmed();
    [IntegrationFact(TestEnv.RabbitMqVar)] public Task T02_키가_많으면_16큐에_퍼진다() => Many_keys_spread_over_partitions();
}

[Trait("Category", "Integration")]
public sealed class RabbitTopologyTests : IAsyncLifetime
{
    private RabbitRig? _rig;

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _rig?.DisposeAsync().AsTask() ?? Task.CompletedTask;

    [IntegrationFact(TestEnv.RabbitMqVar)]
    public async Task T01_두_번_선언은_성공_인자가_다른_기존_큐는_기동_실패()
    {
        _rig = new RabbitRig();
        await _rig.Declarer.RunAsync(default);
        await _rig.Declarer.RunAsync(default);

        var other = new RabbitRig();
        var connection = await other.Connections.GetAsync(default);
        await using (var ch = await connection.CreateChannelAsync())
        {
            await ch.QueueDeclareAsync(TopologySpec.QueueName(EventCategory.Task, 0, other.Prefix), durable: true, exclusive: false,
                autoDelete: false, arguments: new Dictionary<string, object?> { ["x-queue-type"] = "classic" });
        }

        try
        {
            var act = () => other.Declarer.RunAsync(default);
            (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("406");
        }
        finally
        {
            await other.DisposeAsync();
        }
    }

    [IntegrationFact(TestEnv.RabbitMqVar)]
    public async Task T03_전달_한도_초과는_DLQ()
    {
        _rig = new RabbitRig();
        await _rig.Declarer.RunAsync(default);
        await _rig.Publisher.PublishBatchAsync(
            [new OutgoingMessage(EventCategory.Task, "T-limit", "x/0", EventType.TaskCreated, true, "{}"u8.ToArray())], default);
        var queue = Enumerable.Range(0, TopologySpec.Partitions).Select(p => TopologySpec.QueueName(EventCategory.Task, p, _rig.Prefix))
            .First(q => _rig.Subscriber.GetMessageCountAsync(q, default).Result > 0);

        for (var i = 0; i < 21; i++)
        {
            var got = new TaskCompletionSource();
            var sub = await _rig.Subscriber.SubscribeAsync(queue, 10, (_, _) => { got.TrySetResult(); return ValueTask.CompletedTask; }, default);
            await got.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await sub.DisposeAsync();   // ack 없이 채널 닫기
        }

        var dlq = TopologySpec.DlqName(_rig.Prefix);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (await _rig.Subscriber.GetMessageCountAsync(dlq, default) == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(100);
        (await _rig.Subscriber.GetMessageCountAsync(dlq, default)).Should().Be(1);
    }

    [IntegrationFact(TestEnv.RabbitMqVar)]
    public async Task 발행_확인은_배열_순서대로_들어간다()
    {
        _rig = new RabbitRig();
        await _rig.Declarer.RunAsync(default);
        var messages = Enumerable.Range(0, 1000)
            .Select(i => new OutgoingMessage(EventCategory.Task, "R-order", $"o/{i}", EventType.RobotStatusChanged, true, "{}"u8.ToArray()))
            .ToList();
        var outcome = await _rig.Publisher.PublishBatchAsync(messages, default);
        outcome.ReliableConfirmed.Should().Be(1000);

        var queue = Enumerable.Range(0, TopologySpec.Partitions).Select(p => TopologySpec.QueueName(EventCategory.Task, p, _rig.Prefix))
            .First(q => _rig.Subscriber.GetMessageCountAsync(q, default).Result > 0);
        var got = new ConcurrentQueue<string>();
        await using var sub = await _rig.Subscriber.SubscribeAsync(queue, 50, async (d, ct) =>
        {
            got.Enqueue(d.MessageId);
            await Task.CompletedTask;
        }, default);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (got.Count < 50 && DateTime.UtcNow < deadline) await Task.Delay(20);
        got.Take(50).Should().Equal(messages.Take(50).Select(m => m.MessageId));
    }
}
