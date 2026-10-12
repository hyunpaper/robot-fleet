using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using RobotFleet.EventWorker;
using RobotFleet.Infrastructure.InMemory;
using RobotFleet.Ingest;
using RobotFleet.Shared.Messaging;
using RobotFleet.Shared.Pipeline;

namespace RobotFleet.Pipeline.Tests;

/// 브로커 · 저장소 · Ingest · 워커를 메모리로 조립한 시험대
internal sealed class WorkerRig
{
    public InMemoryBroker Broker { get; } = new();

    public InMemoryEventStore Store { get; } = new();

    public RecordingStore Recording { get; }

    public IngestService Ingest { get; }

    public WorkerRig()
    {
        Recording = new RecordingStore(Store);
        Ingest = new IngestService(new InMemoryIdemStore(TimeProvider.System), Broker, TimeProvider.System,
            NullLogger<IngestService>.Instance);
    }

    public static WorkerOptions Options(string id = "w-1") => new()
    {
        WorkerId = id,
        HeartbeatInterval = TimeSpan.Zero,
        RebalanceInterval = TimeSpan.FromMilliseconds(20),
        DbRetryMin = TimeSpan.FromMilliseconds(5),
        DbRetryMax = TimeSpan.FromMilliseconds(40),
    };

    public AssignmentCoordinator Worker(IPartitionRoster roster, WorkerOptions? options = null, IPartitionSubscriber? subscriber = null,
                                        TimeProvider? time = null)
    {
        options ??= Options();
        var handlers = new EventHandlers(Recording, Broker, options, NullLogger<EventHandlers>.Instance);
        return new AssignmentCoordinator(roster, subscriber ?? Broker, handlers, options, time ?? TimeProvider.System,
            NullLogger<AssignmentCoordinator>.Instance);
    }

    public static IPartitionRoster Single() => new InMemoryPartitionRoster(TimeProvider.System, configured: false);

    public async Task SendAsync(EventBatch batch)
    {
        var resp = await Ingest.HandleAsync(TestBatches.Body(batch), default);
        resp.StatusCode.Should().Be(202, resp.Body.Error);
    }

    public Task DrainedAsync() => Until(() => Broker.PartitionBacklog() == 0);

    public static async Task Until(Func<bool> condition, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("조건을 기다리다 시간 초과");
            await Task.Delay(5);
        }
    }
}

/// 저장 호출을 기록하고, 필요하면 커밋 전 · 후에 멈춘다
internal sealed class RecordingStore(IEventStore inner) : IEventStore
{
    public ConcurrentQueue<int> BatchSizes { get; } = new();

    /// 설정되면 저장 **전에** 이 작업이 끝날 때까지 기다린다
    public TaskCompletionSource? HoldBefore { get; set; }

    /// 설정되면 저장 **후**(커밋됨) 이 작업이 끝날 때까지 기다린다
    public TaskCompletionSource? HoldAfter { get; set; }

    public TaskCompletionSource Entered { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<StoreResult> StoreBatchAsync(StoreBatch batch, CancellationToken ct)
    {
        Entered.TrySetResult();
        if (HoldBefore is { } before) await before.Task.WaitAsync(ct);
        var result = await inner.StoreBatchAsync(batch, ct);
        BatchSizes.Enqueue(batch.Telemetry.Count + batch.TaskEvents.Count + batch.StatusChanges.Count + batch.Conflicts.Count);
        if (HoldAfter is { } after) await after.Task.WaitAsync(ct);
        return result;
    }

    public void ResetEntered() => Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<IReadOnlyDictionary<string, RobotStateRow>> GetRobotStatesAsync(string mapId, IReadOnlyCollection<string> robotIds,
        CancellationToken ct) => inner.GetRobotStatesAsync(mapId, robotIds, ct);

    public Task<IReadOnlySet<string>> GetExistingStatusEventIdsAsync(IReadOnlyCollection<string> eventIds, CancellationToken ct) =>
        inner.GetExistingStatusEventIdsAsync(eventIds, ct);
}

/// 구독 · 해제 순서를 기록한다(R-03)
internal sealed class RecordingSubscriber(IPartitionSubscriber inner) : IPartitionSubscriber
{
    public ConcurrentQueue<string> Log { get; } = new();

    public async Task<IPartitionSubscription> SubscribeAsync(string queue, ushort prefetch,
        Func<Delivery, CancellationToken, ValueTask> onDelivery, CancellationToken ct)
    {
        Log.Enqueue("+" + queue);
        return new Sub(await inner.SubscribeAsync(queue, prefetch, onDelivery, ct), this);
    }

    public Task<long> GetMessageCountAsync(string queue, CancellationToken ct) => inner.GetMessageCountAsync(queue, ct);

    private sealed class Sub(IPartitionSubscription inner, RecordingSubscriber owner) : IPartitionSubscription
    {
        public string Queue => inner.Queue;

        public Task AckUpToAsync(ulong deliveryTag, CancellationToken ct) => inner.AckUpToAsync(deliveryTag, ct);

        public Task RejectAsync(ulong deliveryTag, CancellationToken ct) => inner.RejectAsync(deliveryTag, ct);

        public ValueTask DisposeAsync()
        {
            owner.Log.Enqueue("-" + inner.Queue);
            return inner.DisposeAsync();
        }
    }
}
