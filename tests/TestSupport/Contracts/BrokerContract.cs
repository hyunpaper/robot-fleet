using System.Collections.Concurrent;
using System.Text;
using FluentAssertions;
using RobotFleet.Shared.Messaging;
using RobotFleet.Shared.Pipeline;

namespace RobotFleet.TestSupport.Contracts;

/// IEventPublisher + IPartitionSubscriber 계약(C-04): confirm · 같은 키 같은 큐 · prefetch · multiple ack ·
/// SAC 활성 전환과 채널 닫기 반환 · reject → DLQ. InMemoryBroker(L1) · RabbitMQ(L2)가 같은 표를 통과한다. 10 §12
public abstract class BrokerContract
{
    protected static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    /// 토폴로지가 선언된 상태의 발행자 · 구독자와 이름 접두사
    protected abstract Task<(IEventPublisher Publisher, IPartitionSubscriber Subscriber, string Prefix)> CreateAsync();

    protected async Task Same_key_lands_in_one_queue_in_order()
    {
        var (pub, sub, prefix) = await CreateAsync();
        var messages = Enumerable.Range(0, 20).Select(i => Message(EventCategory.Task, "T-same", $"m/{i}")).ToList();
        var outcome = await pub.PublishBatchAsync(messages, default);
        outcome.ReliableConfirmed.Should().Be(20);
        outcome.ReliableFailed.Should().Be(0);

        var counts = await CountsAsync(sub, EventCategory.Task, prefix);
        counts.Values.Should().ContainSingle(c => c == 20);
        counts.Values.Sum().Should().Be(20);

        var queue = counts.Single(c => c.Value == 20).Key;
        var got = new ConcurrentQueue<string>();
        await using var s = await sub.SubscribeAsync(queue, 50, (d, _) => { got.Enqueue(d.MessageId); return ValueTask.CompletedTask; }, default);
        await Until(() => got.Count == 20);
        got.Should().Equal(messages.Select(m => m.MessageId));
    }

    protected async Task Many_keys_spread_over_partitions()
    {
        var (pub, sub, prefix) = await CreateAsync();
        var messages = Enumerable.Range(0, 2000).Select(i => Message(EventCategory.Telemetry, $"R-{i:D4}", $"t/{i}")).ToList();
        var outcome = await pub.PublishBatchAsync(messages, default);
        (outcome.TelemetryConfirmed + outcome.TelemetryDropped).Should().Be(2000);
        var counts = await CountsAsync(sub, EventCategory.Telemetry, prefix);
        counts.Values.Count(c => c > 0).Should().BeGreaterThan(8);
    }

    protected async Task Prefetch_limits_unacked_and_multiple_ack_releases()
    {
        var (pub, sub, prefix) = await CreateAsync();
        await pub.PublishBatchAsync(Enumerable.Range(0, 12).Select(i => Message(EventCategory.Task, "T-pf", $"p/{i}")).ToList(), default);
        var queue = (await CountsAsync(sub, EventCategory.Task, prefix)).Single(c => c.Value > 0).Key;

        var got = new ConcurrentQueue<Delivery>();
        await using var s = await sub.SubscribeAsync(queue, 5, (d, _) => { got.Enqueue(d); return ValueTask.CompletedTask; }, default);
        await Until(() => got.Count == 5);
        await Task.Delay(200);
        got.Should().HaveCount(5);   // 미확인 5건에서 멈춘다

        await s.AckUpToAsync(got.Last().DeliveryTag, default);   // multiple
        await Until(() => got.Count == 10);
        await s.AckUpToAsync(got.Last().DeliveryTag, default);
        await Until(() => got.Count == 12);
        await s.AckUpToAsync(got.Last().DeliveryTag, default);
        await Until(async () => await sub.GetMessageCountAsync(queue, default) == 0);
    }

    protected async Task Single_active_consumer_hands_over_unacked_on_close()
    {
        var (pub, sub, prefix) = await CreateAsync();
        await pub.PublishBatchAsync(Enumerable.Range(0, 6).Select(i => Message(EventCategory.Conflict, "m-sac", $"s/{i}")).ToList(), default);
        var queue = (await CountsAsync(sub, EventCategory.Conflict, prefix)).Single(c => c.Value > 0).Key;

        var first = new ConcurrentQueue<Delivery>();
        var second = new ConcurrentQueue<Delivery>();
        var a = await sub.SubscribeAsync(queue, 50, (d, _) => { first.Enqueue(d); return ValueTask.CompletedTask; }, default);
        await Until(() => first.Count == 6);
        await using var b = await sub.SubscribeAsync(queue, 50, (d, _) => { second.Enqueue(d); return ValueTask.CompletedTask; }, default);
        await Task.Delay(200);
        second.Should().BeEmpty();   // SAC: 대기

        await a.AckUpToAsync(first.ElementAt(1).DeliveryTag, default);   // s/0, s/1 확인
        await a.DisposeAsync();                                          // 채널 닫기 → s/2~s/5 반환
        await Until(() => second.Count == 4);
        second.Select(d => d.MessageId).Should().Equal("s/2", "s/3", "s/4", "s/5");
        second.Should().OnlyContain(d => d.Redelivered);
    }

    protected async Task Reject_goes_to_dlq()
    {
        var (pub, sub, prefix) = await CreateAsync();
        await pub.PublishBatchAsync([Message(EventCategory.Task, "T-dlq", "d/0")], default);
        var queue = (await CountsAsync(sub, EventCategory.Task, prefix)).Single(c => c.Value > 0).Key;
        var got = new ConcurrentQueue<Delivery>();
        await using (var s = await sub.SubscribeAsync(queue, 10, (d, _) => { got.Enqueue(d); return ValueTask.CompletedTask; }, default))
        {
            await Until(() => got.Count == 1);
            await s.RejectAsync(got.Single().DeliveryTag, default);
        }

        await Until(async () => await sub.GetMessageCountAsync(TopologySpec.DlqName(prefix), default) == 1);
        (await sub.GetMessageCountAsync(queue, default)).Should().Be(0);
    }

    protected async Task Publish_to_invalid_queue_is_confirmed()
    {
        var (pub, sub, prefix) = await CreateAsync();
        await pub.PublishToQueueAsync(TopologySpec.InvalidQueueName(prefix), Message(EventCategory.Task, "x", "i/0"), default);
        await Until(async () => await sub.GetMessageCountAsync(TopologySpec.InvalidQueueName(prefix), default) == 1);
    }

    protected static OutgoingMessage Message(EventCategory category, string key, string id) =>
        new(category, key, id, category == EventCategory.Telemetry ? EventType.RobotTelemetry : EventType.TaskCreated,
            category != EventCategory.Telemetry, Encoding.UTF8.GetBytes($$"""{"id":"{{id}}"}"""));

    protected static async Task<Dictionary<string, long>> CountsAsync(IPartitionSubscriber sub, EventCategory category, string prefix)
    {
        var counts = new Dictionary<string, long>();
        for (var p = 0; p < TopologySpec.Partitions; p++)
        {
            var q = TopologySpec.QueueName(category, p, prefix);
            counts[q] = await sub.GetMessageCountAsync(q, default);
        }

        return counts;
    }

    protected static Task Until(Func<bool> condition) => Until(() => Task.FromResult(condition()));

    protected static async Task Until(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("조건을 기다리다 시간 초과");
            await Task.Delay(10);
        }
    }
}
