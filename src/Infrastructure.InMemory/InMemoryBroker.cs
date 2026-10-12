using System.Text;
using System.Threading.Channels;
using RobotFleet.Shared.Messaging;
using RobotFleet.Shared.Pipeline;

namespace RobotFleet.Infrastructure.InMemory;

public enum PublishFault
{
    /// 브로커가 거절. 큐에 들어가지 않는다
    Nack,

    /// 확인을 못 받음. 실제로는 **큐에 들어갔다**(재전송하면 중복이 된다)
    ConfirmTimeout,

    /// 이 메시지부터 배치 끝까지 채널이 닫힘. 아무것도 들어가지 않는다
    ChannelClosed,
}

/// consistent-hash · SAC · prefetch · multiple ack · 채널 닫기 반환(전달 횟수 증가) · x-delivery-limit → DLQ ·
/// reject → DLX · reject-publish · drop-head를 실제 RabbitMQ 의미론대로 흉내 낸다. 10 §12
public sealed class InMemoryBroker : IEventPublisher, IPartitionSubscriber
{
    private readonly object _gate = new();
    private readonly Dictionary<string, BrokerQueue> _queues = new(StringComparer.Ordinal);
    private readonly List<(PublishFault Fault, EventCategory? Category)> _faults = [];
    private long _returned;
    private int _consumerSeq;

    public InMemoryBroker(string topologyPrefix = "")
    {
        TopologyPrefix = topologyPrefix;
        foreach (var q in TopologySpec.Build(topologyPrefix).Queues)
            _queues[q.Name] = new BrokerQueue(q.Name, q.Arguments, TopologySpec.DlqName(topologyPrefix));
    }

    public string TopologyPrefix { get; }

    /// 채널 닫기로 반환된 메시지 수(누적)
    public long Returned
    {
        get { lock (_gate) return _returned; }
    }

    /// 다음 count개 메시지(범주 지정 시 그 범주만)에 장애를 건다
    public void InjectPublishFault(PublishFault fault, int count = 1, EventCategory? category = null)
    {
        lock (_gate)
        {
            for (var i = 0; i < count; i++)
                _faults.Add((fault, category));
        }
    }

    public void SetMaxLengthBytes(string queue, long bytes)
    {
        lock (_gate) Queue(queue).MaxBytes = bytes;
    }

    public long Depth(string queue)
    {
        lock (_gate) return Queue(queue).Ready.Count;
    }

    public int UnackedCount(string queue)
    {
        lock (_gate) return Queue(queue).Consumers.Sum(c => c.Unacked.Count);
    }

    /// 파티션 큐 전체의 대기 + 미확인 메시지 수. 0이면 소비가 끝났다
    public long PartitionBacklog()
    {
        lock (_gate)
            return _queues.Values.Where(q => q.IsPartition).Sum(q => q.Ready.Count + q.Consumers.Sum(c => c.Unacked.Count));
    }

    public int ConsumerCount(string queue)
    {
        lock (_gate) return Queue(queue).Consumers.Count;
    }

    /// 대기 메시지(본문 포함) 스냅숏
    public IReadOnlyList<OutgoingMessage> Messages(string queue)
    {
        lock (_gate) return Queue(queue).Ready.Select(m => m.Message).ToList();
    }

    public IReadOnlyList<OutgoingMessage> DeadLetters() => Messages(TopologySpec.DlqName(TopologyPrefix));

    public IReadOnlyList<OutgoingMessage> InvalidMessages() => Messages(TopologySpec.InvalidQueueName(TopologyPrefix));

    /// consistent-hash exchange 흉내: 같은 키 → 항상 같은 파티션
    public static int PartitionOf(string routingKey)
    {
        var hash = 2166136261u;
        foreach (var b in Encoding.UTF8.GetBytes(routingKey))
            hash = (hash ^ b) * 16777619u;
        return (int)(hash % TopologySpec.Partitions);
    }

    public string QueueFor(EventCategory category, string routingKey) =>
        TopologySpec.QueueName(category, PartitionOf(routingKey), TopologyPrefix);

    public Task<PublishOutcome> PublishBatchAsync(IReadOnlyList<OutgoingMessage> messages, CancellationToken ct)
    {
        int relOk = 0, relFail = 0, telOk = 0, telDrop = 0;
        var reasons = new Dictionary<string, int>(StringComparer.Ordinal);
        var touched = new HashSet<BrokerQueue>();
        lock (_gate)
        {
            var closed = false;
            foreach (var m in messages)
            {
                var queue = Queue(QueueFor(m.Category, m.RoutingKey));
                string? failure;
                if (closed)
                {
                    failure = "channel_closed";
                }
                else
                {
                    switch (TakeFault(m.Category))
                    {
                        case PublishFault.Nack:
                            failure = "nack";
                            break;
                        case PublishFault.ChannelClosed:
                            closed = true;
                            failure = "channel_closed";
                            break;
                        case PublishFault.ConfirmTimeout:
                            Enqueue(queue, m);
                            touched.Add(queue);
                            failure = "timeout";
                            break;
                        default:
                            failure = Enqueue(queue, m) ? null : "reject_publish";
                            touched.Add(queue);
                            break;
                    }
                }

                var telemetry = m.Category == EventCategory.Telemetry;
                if (failure is null)
                {
                    if (telemetry) telOk++;
                    else relOk++;
                    continue;
                }

                if (telemetry)
                {
                    telDrop++;
                    reasons[failure] = reasons.GetValueOrDefault(failure) + 1;
                }
                else
                {
                    relFail++;
                }
            }
        }

        foreach (var q in touched)
            Pump(q);
        return Task.FromResult(new PublishOutcome(relOk, relFail, telOk, telDrop, reasons));
    }

    public Task PublishToQueueAsync(string queue, OutgoingMessage message, CancellationToken ct)
    {
        BrokerQueue q;
        lock (_gate)
        {
            q = Queue(queue);
            if (TakeFault(message.Category) is PublishFault.Nack or PublishFault.ChannelClosed)
                throw new InvalidOperationException($"{queue} 발행 확인 실패(주입)");
            if (!Enqueue(q, message))
                throw new InvalidOperationException($"{queue} reject-publish");
        }

        Pump(q);
        return Task.CompletedTask;
    }

    public Task<IPartitionSubscription> SubscribeAsync(string queue, ushort prefetch,
        Func<Delivery, CancellationToken, ValueTask> onDelivery, CancellationToken ct)
    {
        Consumer consumer;
        lock (_gate)
        {
            var q = Queue(queue);
            consumer = new Consumer(this, q, prefetch, onDelivery, ++_consumerSeq);
            q.Consumers.Add(consumer);   // SAC: 먼저 붙은 소비자만 활성, 나머지는 대기
        }

        consumer.Start();
        Pump(consumer.Queue);
        return Task.FromResult<IPartitionSubscription>(consumer);
    }

    public Task<long> GetMessageCountAsync(string queue, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult((long)Queue(queue).Ready.Count);
    }

    private BrokerQueue Queue(string name) =>
        _queues.TryGetValue(name, out var q) ? q : throw new InvalidOperationException($"선언되지 않은 큐: {name}");

    private PublishFault? TakeFault(EventCategory category)
    {
        var i = _faults.FindIndex(f => f.Category is null || f.Category == category);
        if (i < 0) return null;
        var fault = _faults[i].Fault;
        _faults.RemoveAt(i);
        return fault;
    }

    // false = reject-publish(quorum 용량 초과)
    private static bool Enqueue(BrokerQueue q, OutgoingMessage m)
    {
        var size = m.Body.Length;
        if (q.MaxBytes is { } max && q.Bytes + size > max)
            return false;
        if (q.MaxLength is { } maxLen && q.Ready.Count >= maxLen)
        {
            q.Bytes -= q.Ready.First!.Value.Message.Body.Length;   // drop-head
            q.Ready.RemoveFirst();
        }

        q.Ready.AddLast(new StoredMessage(m));
        q.Bytes += size;
        return true;
    }

    private void Pump(BrokerQueue q)
    {
        lock (_gate)
        {
            var active = q.Consumers.FirstOrDefault();
            if (active is null) return;
            while (active.Unacked.Count < active.Prefetch && q.Ready.First is { } node)
            {
                q.Ready.RemoveFirst();
                var stored = node.Value;
                var tag = ++active.LastTag;
                active.Unacked.Add(tag, stored);
                active.Inbox.Writer.TryWrite(new Delivery(q.Name, tag, stored.Message.MessageId, stored.Message.EventType,
                    FleetSchema.Version, stored.Message.Body, stored.DeliveryCount > 0, stored.DeliveryCount));
            }
        }
    }

    private void Ack(Consumer c, ulong tag)
    {
        lock (_gate)
        {
            if (c.Closed || !c.Unacked.ContainsKey(tag))
                throw new InvalidOperationException($"PRECONDITION_FAILED - unknown delivery tag {tag}");
            foreach (var t in c.Unacked.Keys.Where(t => t <= tag).ToList())
            {
                c.Queue.Bytes -= c.Unacked[t].Message.Body.Length;
                c.Unacked.Remove(t);
            }
        }

        Pump(c.Queue);
    }

    private void Reject(Consumer c, ulong tag)
    {
        lock (_gate)
        {
            if (c.Closed || !c.Unacked.Remove(tag, out var stored))
                throw new InvalidOperationException($"PRECONDITION_FAILED - unknown delivery tag {tag}");
            c.Queue.Bytes -= stored.Message.Body.Length;
            DeadLetter(c.Queue, stored);
        }

        Pump(c.Queue);
    }

    // 채널 닫기: 미확인분을 원래 순서대로 큐 앞에 되돌리고 전달 횟수 +1. 한도를 넘으면 DLQ
    private void Close(Consumer c)
    {
        lock (_gate)
        {
            if (c.Closed) return;
            c.Closed = true;
            foreach (var (_, stored) in c.Unacked.Reverse())
            {
                stored.DeliveryCount++;
                _returned++;
                if (c.Queue.DeliveryLimit is { } limit && stored.DeliveryCount > limit)
                {
                    c.Queue.Bytes -= stored.Message.Body.Length;
                    DeadLetter(c.Queue, stored);
                }
                else
                {
                    c.Queue.Ready.AddFirst(stored);
                }
            }

            c.Unacked.Clear();
            c.Queue.Consumers.Remove(c);
            c.Inbox.Writer.TryComplete();
        }

        Pump(c.Queue);
    }

    private void DeadLetter(BrokerQueue from, StoredMessage stored)
    {
        if (!from.HasDlx) return;   // DLX 없는 큐(dlq · invalid)의 reject는 버림
        var dlq = Queue(from.DlqName);
        dlq.Ready.AddLast(stored);
        dlq.Bytes += stored.Message.Body.Length;
    }

    private sealed class BrokerQueue
    {
        public BrokerQueue(string name, IReadOnlyDictionary<string, object> args, string dlqName)
        {
            Name = name;
            DlqName = dlqName;
            IsPartition = args.ContainsKey("x-single-active-consumer");
            HasDlx = args.ContainsKey("x-dead-letter-exchange");
            DeliveryLimit = args.TryGetValue("x-delivery-limit", out var d) ? Convert.ToInt64(d) : null;
            MaxBytes = args.TryGetValue("x-max-length-bytes", out var b) ? Convert.ToInt64(b) : null;
            MaxLength = args.TryGetValue("x-max-length", out var l) ? Convert.ToInt64(l) : null;
        }

        public string Name { get; }

        public string DlqName { get; }

        public bool IsPartition { get; }

        public bool HasDlx { get; }

        public long? DeliveryLimit { get; }

        public long? MaxBytes { get; set; }

        public long? MaxLength { get; }

        public long Bytes { get; set; }

        public LinkedList<StoredMessage> Ready { get; } = new();

        public List<Consumer> Consumers { get; } = [];
    }

    private sealed class StoredMessage(OutgoingMessage message)
    {
        public OutgoingMessage Message { get; } = message;

        public long DeliveryCount { get; set; }
    }

    private sealed class Consumer : IPartitionSubscription
    {
        private readonly InMemoryBroker _broker;
        private readonly Func<Delivery, CancellationToken, ValueTask> _onDelivery;
        private readonly CancellationTokenSource _cts = new();
        private Task _pump = Task.CompletedTask;

        public Consumer(InMemoryBroker broker, BrokerQueue queue, ushort prefetch,
                        Func<Delivery, CancellationToken, ValueTask> onDelivery, int id)
        {
            _broker = broker;
            Queue = queue;
            Prefetch = prefetch;
            _onDelivery = onDelivery;
            Id = id;
        }

        public int Id { get; }

        public BrokerQueue Queue { get; }

        string IPartitionSubscription.Queue => Queue.Name;

        public ushort Prefetch { get; }

        public ulong LastTag { get; set; }

        public SortedDictionary<ulong, StoredMessage> Unacked { get; } = [];

        public Channel<Delivery> Inbox { get; } = Channel.CreateUnbounded<Delivery>(new UnboundedChannelOptions { SingleReader = true });

        public bool Closed { get; set; }

        // RabbitMQ 클라이언트처럼 채널 하나의 전달은 순서대로 하나씩 처리기에 넘긴다
        public void Start() => _pump = Task.Run(async () =>
        {
            try
            {
                await foreach (var d in Inbox.Reader.ReadAllAsync(_cts.Token))
                    await _onDelivery(d, _cts.Token);
            }
            catch (OperationCanceledException)
            {
            }
        });

        public Task AckUpToAsync(ulong deliveryTag, CancellationToken ct)
        {
            _broker.Ack(this, deliveryTag);
            return Task.CompletedTask;
        }

        public Task RejectAsync(ulong deliveryTag, CancellationToken ct)
        {
            _broker.Reject(this, deliveryTag);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _broker.Close(this);
            _cts.Cancel();
            return ValueTask.CompletedTask;
        }
    }
}
