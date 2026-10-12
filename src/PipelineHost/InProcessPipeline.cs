using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using RobotFleet.EventWorker;
using RobotFleet.Infrastructure.InMemory;
using RobotFleet.Ingest;
using RobotFleet.Shared.Map;
using RobotFleet.Shared.Messaging;
using RobotFleet.Shared.Pipeline;
using RobotFleet.Simulator;
using RobotFleet.Simulator.Events;
using RobotFleet.Simulator.Fleet;

namespace RobotFleet.PipelineHost;

/// 인프라 없이 한 프로세스에서 Simulator → Ingest → 브로커 → 워커 → 저장소 전체를 돌린다. 10 §9 in-process 모드
public sealed class InProcessPipeline : IAsyncDisposable
{
    private readonly ConcurrentQueue<EventBatch> _accepted = new();
    private readonly Dictionary<string, (AssignmentCoordinator Worker, CancellationTokenSource Cts, Task Run)> _workers = [];

    /// workers > 1이면 명부(메모리) 기반 배정, 1이면 단일 워커가 전부
    public InProcessPipeline(GridMap map, FleetOptions fleet, bool sharedRoster = false)
    {
        Roster = new InMemoryPartitionRoster(TimeProvider.System, configured: sharedRoster);
        Ingest = new IngestService(Idem, Broker, TimeProvider.System, NullLogger<IngestService>.Instance);
        var client = new DelegateIngestClient(async (body, ct) =>
        {
            var response = await Ingest.HandleAsync(body, ct);
            if (response.StatusCode == 202)
                _accepted.Enqueue(FleetJson.Deserialize<EventBatch>(body.Span));   // 202 이후 = 송신 집합(10 §5)
            return response.ToResult();
        });
        Sender = new EventBatchSender(client, null,
            new EventSenderOptions { BackoffMin = TimeSpan.FromMilliseconds(5), BackoffMax = TimeSpan.FromMilliseconds(50) },
            TimeProvider.System);
        Simulation = new FleetSimulation(map, fleet, Sender, new NoDelay(), null);
    }

    public InMemoryBroker Broker { get; } = new();

    public InMemoryEventStore Store { get; } = new();

    public InMemoryIdemStore Idem { get; } = new(TimeProvider.System);

    public InMemoryPartitionRoster Roster { get; }

    public IngestService Ingest { get; }

    public EventBatchSender Sender { get; }

    public FleetSimulation Simulation { get; }

    public IReadOnlyCollection<string> WorkerIds => _workers.Keys;

    public AssignmentCoordinator StartWorker(string workerId)
    {
        var options = new WorkerOptions
        {
            WorkerId = workerId,
            HeartbeatInterval = TimeSpan.FromMilliseconds(20),
            RebalanceInterval = TimeSpan.FromMilliseconds(20),
            DbRetryMin = TimeSpan.FromMilliseconds(5),
            DbRetryMax = TimeSpan.FromMilliseconds(50),
        };
        var handlers = new EventHandlers(Store, Broker, options, NullLogger<EventHandlers>.Instance);
        var worker = new AssignmentCoordinator(Roster, Broker, handlers, options, TimeProvider.System,
            NullLogger<AssignmentCoordinator>.Instance);
        var cts = new CancellationTokenSource();
        _workers[workerId] = (worker, cts, worker.RunAsync(cts.Token));
        return worker;
    }

    /// 프로세스 강제 종료: ack 없이 채널을 닫는다. 명부 만료(30초)는 탈퇴로 앞당긴다
    public async Task KillWorkerAsync(string workerId)
    {
        var (worker, cts, run) = _workers[workerId];
        _workers.Remove(workerId);
        await worker.KillAsync();
        await cts.CancelAsync();
        await run;
        await Roster.LeaveAsync(workerId, default);
    }

    /// 시뮬레이션 → 송신 버퍼 비우기 → 큐 소비 완료까지
    public async Task<PipelineReport> RunAsync(TimeSpan drainTimeout, CancellationToken ct = default)
    {
        using var senderCts = new CancellationTokenSource();
        var sendLoop = Sender.RunAsync(senderCts.Token);
        var result = await Simulation.RunAsync(ct);
        var unsent = await Sender.DrainAsync(ct);
        await senderCts.CancelAsync();
        await sendLoop;

        var deadline = DateTime.UtcNow + drainTimeout;
        while (Broker.PartitionBacklog() > 0 && DateTime.UtcNow < deadline)
            await Task.Delay(20, ct);

        return Reconcile(result, unsent);
    }

    /// 송신 집합(202 받은 배치) − 적재 집합. 10 §10과 같은 규칙을 메모리에서
    public PipelineReport Reconcile(FleetRunResult result, int unsent)
    {
        var receipts = Store.ReceiptIds();
        long taskSent = 0, conflictSent = 0, telemetrySent = 0, telemetryMissing = 0;
        var missing = new List<string>();
        foreach (var batch in _accepted.DistinctBy(b => b.BatchId))
        {
            for (var i = 0; i < batch.Events.Count; i++)
            {
                var e = batch.Events[i];
                var id = $"{batch.BatchId}/{i}";
                switch (EventCategories.Of(e.EventType))
                {
                    case EventCategory.Telemetry:
                        telemetrySent++;
                        if (!Store.HasTelemetry(batch.MapId, RoutingKeys.Of(e.EventType, e.Payload, batch.MapId), batch.Tick))
                            telemetryMissing++;
                        break;
                    case EventCategory.Task:
                        taskSent++;
                        if (!receipts.Contains(id)) missing.Add(id);
                        break;
                    default:
                        if (e.EventType == EventType.PathReplanned) break;
                        conflictSent++;
                        if (!receipts.Contains(id)) missing.Add(id);
                        break;
                }
            }
        }

        return new PipelineReport(result, unsent, taskSent, conflictSent, telemetrySent, missing, telemetryMissing,
            Store.StatusLog().Count(s => !s.Valid), Broker.PartitionBacklog(), Broker.DeadLetters().Count, Broker.Returned);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var (_, cts, run) in _workers.Values)
        {
            await cts.CancelAsync();
            await run;
        }

        _workers.Clear();
        await Sender.DisposeAsync();
    }
}

/// Missing = task · conflict 누락 eventId (유실 0이면 빈 목록)
public sealed record PipelineReport(
    FleetRunResult Simulation, int Unsent, long TaskSent, long ConflictSent, long TelemetrySent,
    IReadOnlyList<string> Missing, long TelemetryMissing, long InvalidTransitions, long Backlog, long DeadLettered, long Returned)
{
    public bool LossFree => Unsent == 0 && Missing.Count == 0 && Backlog == 0;
}
