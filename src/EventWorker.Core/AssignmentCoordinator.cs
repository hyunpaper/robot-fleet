using Microsoft.Extensions.Logging;
using RobotFleet.Shared.Messaging;
using RobotFleet.Shared.Pipeline;

namespace RobotFleet.EventWorker;

/// 명부 기반 파티션 배정 · 재배정(새 몫 구독 → 옛 몫 취소). 배정 단위는 파티션 번호, 번호 하나 = 범주별 큐 3개. 10 §7
public sealed class AssignmentCoordinator
{
    private static readonly EventCategory[] Categories = [EventCategory.Telemetry, EventCategory.Task, EventCategory.Conflict];

    private readonly IPartitionRoster? _roster;
    private readonly IPartitionSubscriber _subscriber;
    private readonly EventHandlers _handlers;
    private readonly WorkerOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<AssignmentCoordinator> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<int, List<PartitionProcessor>> _owned = [];
    private SortedSet<int>? _candidate;
    private RosterView? _view;
    private long _lastBeat = long.MinValue;
    private volatile bool _killed;

    /// roster가 null = Redis 미설정 → 단일 워커가 48큐 전부(10 §7). 대체 구현을 두지 않는다
    public AssignmentCoordinator(IPartitionSubscriber subscriber, EventHandlers handlers, WorkerOptions options,
                                 TimeProvider time, ILogger<AssignmentCoordinator> logger, IPartitionRoster? roster = null)
    {
        _roster = roster;
        _subscriber = subscriber;
        _handlers = handlers;
        _options = options;
        _time = time;
        _logger = logger;
        WorkerId = string.IsNullOrWhiteSpace(options.WorkerId)
            ? $"{Environment.MachineName}-{time.GetUtcNow().ToUnixTimeMilliseconds()}"
            : options.WorkerId;
    }

    public string WorkerId { get; }

    /// 현재 소유한 파티션 번호(0~15)
    public IReadOnlyCollection<int> OwnedPartitions
    {
        get
        {
            lock (_owned) return _owned.Keys.Order().ToList();
        }
    }

    /// 처리 · ack한 메시지 수 합(테스트 관찰용)
    public long Acked
    {
        get
        {
            lock (_owned) return _owned.Values.SelectMany(p => p).Sum(p => p.Acked);
        }
    }

    /// 취소될 때까지 heartbeat · 재계산 · 구독 전환. 종료 시 모든 구독을 마무리하고 명부에서 나간다
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && !_killed)
            {
                await RebalanceOnceAsync(ct);
                await Task.Delay(_options.RebalanceInterval, _time, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        finally
        {
            await StopAllAsync(drain: true);
            if (_roster is not null && !_killed)
            {
                try
                {
                    await _roster.LeaveAsync(WorkerId, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "명부 탈퇴 실패(30초 뒤 만료로 빠진다)");
                }
            }
        }
    }

    /// 프로세스가 죽은 것과 같게: 이후 재배정을 멈추고, ack 없이 모든 채널을 닫고, 명부에서 나가지 않는다(30초 뒤 만료)
    public async Task KillAsync()
    {
        _killed = true;
        await _gate.WaitAsync();
        try
        {
            await StopAllAsync(drain: false);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task RebalanceOnceAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_killed) return;
            await RestartDeadAsync(ct);
            var desired = await DesiredAsync(ct);
            if (desired is null) return;   // 명부 불통: 현재 몫 고정

            // 2회 연속 같은 결과일 때만 적용. 명부가 없으면(단일 워커) 바로
            if (_roster is not null && (_candidate is null || !_candidate.SetEquals(desired)))
            {
                _candidate = desired;
                return;
            }

            _candidate = desired;
            await ApplyAsync(desired, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    // 채널이 닫혀 끝난 처리기는 같은 큐로 다시 구독한다
    private async Task RestartDeadAsync(CancellationToken ct)
    {
        List<(int Partition, int Index, PartitionProcessor Old)> dead;
        lock (_owned)
            dead = _owned.SelectMany(p => p.Value.Select((proc, i) => (p.Key, i, proc))).Where(x => x.proc.IsDead).ToList();

        foreach (var (partition, index, old) in dead)
        {
            await old.StopAsync(drain: false);
            var fresh = new PartitionProcessor(old.Queue, _subscriber, _handlers, _options, _time, _logger);
            await fresh.StartAsync(ct);
            lock (_owned) _owned[partition][index] = fresh;
            _logger.LogWarning("{Queue} 처리기 재시작", old.Queue);
        }
    }

    private async Task<SortedSet<int>?> DesiredAsync(CancellationToken ct)
    {
        var all = new SortedSet<int>(Enumerable.Range(0, TopologySpec.Partitions));
        if (_roster is null) return all;

        var now = _time.GetTimestamp();
        if (_view is null || _lastBeat == long.MinValue || _time.GetElapsedTime(_lastBeat, now) >= _options.HeartbeatInterval)
        {
            RosterView? view = null;
            try
            {
                view = await _roster.HeartbeatAsync(WorkerId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "명부 heartbeat 실패");
            }

            _lastBeat = now;
            if (view is null)
            {
                // 기동부터 불통이면 전부 구독(SAC가 동시 소비를 막는다). 실행 중 끊기면 현재 몫 고정
                return _view is null && OwnedPartitions.Count == 0 ? all : null;
            }

            _view = view;
        }

        var rank = _view.RankOf(WorkerId);
        if (rank < 0) return null;
        var n = _view.Members.Count;
        return new SortedSet<int>(all.Where(p => p % n == rank));
    }

    private async Task ApplyAsync(SortedSet<int> desired, CancellationToken ct)
    {
        List<int> add, remove;
        lock (_owned)
        {
            add = desired.Where(p => !_owned.ContainsKey(p)).ToList();
            remove = _owned.Keys.Where(p => !desired.Contains(p)).ToList();
        }

        if (add.Count == 0 && remove.Count == 0) return;

        // ① 새 몫을 먼저 구독(SAC라 앞 주인이 있으면 대기로 붙는다)
        foreach (var p in add)
        {
            var processors = new List<PartitionProcessor>();
            foreach (var category in Categories)
            {
                var processor = new PartitionProcessor(TopologySpec.QueueName(category, p, _options.TopologyPrefix),
                    _subscriber, _handlers, _options, _time, _logger);
                await processor.StartAsync(ct);
                processors.Add(processor);
            }

            lock (_owned) _owned[p] = processors;
        }

        // ② 옛 몫은 진행 중 배치를 커밋 · ack한 뒤 채널을 닫는다
        foreach (var p in remove)
        {
            List<PartitionProcessor> processors;
            lock (_owned)
            {
                processors = _owned[p];
                _owned.Remove(p);
            }

            foreach (var processor in processors)
                await processor.StopAsync(drain: true);
        }

        WorkerMetrics.Reassign.Inc();
        WorkerMetrics.PartitionsOwned.Set(OwnedPartitions.Count);
        _logger.LogInformation("{Worker} 몫 전환: +[{Add}] −[{Remove}]", WorkerId, string.Join(',', add), string.Join(',', remove));
    }

    private async Task StopAllAsync(bool drain)
    {
        List<PartitionProcessor> all;
        lock (_owned)
        {
            all = _owned.Values.SelectMany(p => p).ToList();
            _owned.Clear();
        }

        foreach (var processor in all)
            await processor.StopAsync(drain);
        WorkerMetrics.PartitionsOwned.Set(0);
    }
}
