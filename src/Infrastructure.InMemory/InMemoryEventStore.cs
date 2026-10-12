using RobotFleet.Shared.Pipeline;

namespace RobotFleet.Infrastructure.InMemory;

/// PgEventStore와 같은 유니크 · RETURNING 집계 의미론. 배치 하나가 트랜잭션 하나(전부 반영 또는 전부 없음). 10 §6 · §12
public sealed class InMemoryEventStore : IEventStore
{
    private readonly object _gate = new();
    private readonly HashSet<(string Map, string Robot, long Tick, DateTimeOffset At)> _telemetry = [];
    private readonly HashSet<(string Map, string Robot, long Tick)> _telemetryTicks = [];
    private readonly Dictionary<(string Map, string Robot, DateTimeOffset Minute), MetricsRow> _metrics = [];
    private readonly Dictionary<string, TaskEventRow> _taskEvents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskHistoryChange> _taskHistory = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StatusChangeRow> _statusLog = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Map, string Robot), RobotStateRow> _robotState = [];
    private readonly Dictionary<string, ConflictLogChange> _conflicts = new(StringComparer.Ordinal);
    private int _failBefore;
    private int _failAfter;

    /// 다음 count번의 StoreBatchAsync가 아무것도 반영하지 않고 예외(일시 오류)
    public void FailNext(int count)
    {
        lock (_gate)
        {
            _failBefore = count;
        }
    }

    /// 다음 count번의 StoreBatchAsync가 **반영한 뒤** 예외(커밋됐는데 실패로 보임)
    public void FailAfterCommitNext(int count)
    {
        lock (_gate)
        {
            _failAfter = count;
        }
    }

    public int TelemetryCount
    {
        get { lock (_gate) return _telemetry.Count; }
    }

    public bool HasTelemetry(string mapId, string robotId, long tick)
    {
        lock (_gate) return _telemetryTicks.Contains((mapId, robotId, tick));
    }

    /// task_event_log ∪ robot_status_log ∪ conflict_log 의 event_id (Reconcile의 적재 집합)
    public IReadOnlySet<string> ReceiptIds()
    {
        lock (_gate)
        {
            var ids = new HashSet<string>(_taskEvents.Keys, StringComparer.Ordinal);
            ids.UnionWith(_statusLog.Keys);
            ids.UnionWith(_conflicts.Keys);
            return ids;
        }
    }

    public IReadOnlyList<StatusChangeRow> StatusLog()
    {
        lock (_gate) return [.. _statusLog.Values];
    }

    public TaskHistoryChange? TaskHistory(string taskId)
    {
        lock (_gate) return _taskHistory.GetValueOrDefault(taskId);
    }

    public MetricsRow Metrics(string mapId, string robotId, DateTimeOffset minute)
    {
        lock (_gate) return _metrics.GetValueOrDefault((mapId, robotId, StoreMapping.MinuteOf(minute))) ?? new MetricsRow();
    }

    public MetricsRow MetricsTotal(string mapId)
    {
        lock (_gate)
        {
            var total = new MetricsRow();
            foreach (var (key, row) in _metrics.Where(m => m.Key.Map == mapId))
            {
                _ = key;
                total = total.Add(row);
            }

            return total;
        }
    }

    public Task<StoreResult> StoreBatchAsync(StoreBatch batch, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_failBefore > 0)
            {
                _failBefore--;
                throw new InMemoryStoreException("주입된 일시 오류(반영 없음)");
            }

            var result = Apply(batch);
            if (_failAfter > 0)
            {
                _failAfter--;
                throw new InMemoryStoreException("주입된 오류(반영 후)");
            }

            return Task.FromResult(result);
        }
    }

    public Task<IReadOnlyDictionary<string, RobotStateRow>> GetRobotStatesAsync(string mapId,
        IReadOnlyCollection<string> robotIds, CancellationToken ct)
    {
        lock (_gate)
        {
            var found = new Dictionary<string, RobotStateRow>(StringComparer.Ordinal);
            foreach (var robot in robotIds)
            {
                if (_robotState.TryGetValue((mapId, robot), out var row))
                {
                    found[robot] = row;
                }
            }

            return Task.FromResult<IReadOnlyDictionary<string, RobotStateRow>>(found);
        }
    }

    public Task<IReadOnlySet<string>> GetExistingStatusEventIdsAsync(IReadOnlyCollection<string> eventIds, CancellationToken ct)
    {
        lock (_gate)
        {
            IReadOnlySet<string> found = eventIds.Where(_statusLog.ContainsKey).ToHashSet(StringComparer.Ordinal);
            return Task.FromResult(found);
        }
    }

    private StoreResult Apply(StoreBatch batch)
    {
        int telIns = 0, telDup = 0, taskIns = 0, taskDup = 0, stIns = 0, stDup = 0, cfIns = 0, cfDup = 0;

        // 해석 실패는 반영 전에 터져야 한다(트랜잭션 롤백과 같은 결과)
        var tasks = batch.TaskEvents.Select(e => (Row: e, History: StoreMapping.ToHistory(e), Done: StoreMapping.CompletedRobotOf(e))).ToList();
        var conflicts = batch.Conflicts.Select(c => (Row: c, Log: StoreMapping.ToConflict(c))).ToList();

        foreach (var t in batch.Telemetry)
        {
            if (!_telemetry.Add((t.MapId, t.RobotId, t.Tick, t.OccurredAt)))
            {
                telDup++;
                continue;
            }

            telIns++;
            _telemetryTicks.Add((t.MapId, t.RobotId, t.Tick));
            if (StoreMapping.BucketOf(t.Status) is { } bucket)
            {
                AddMetrics(t.MapId, t.RobotId, t.OccurredAt, MetricsRow.Of(bucket, t.TickMs));
            }
        }

        foreach (var (e, history, done) in tasks)
        {
            if (!_taskEvents.TryAdd(e.EventId, e))
            {
                taskDup++;
                continue;
            }

            taskIns++;
            ApplyHistory(history);
            if (done is { } robot)
            {
                AddMetrics(e.MapId, robot, e.OccurredAt, new MetricsRow { TaskCount = 1 });
            }
        }

        foreach (var s in batch.StatusChanges)
        {
            if (!_statusLog.TryAdd(s.EventId, s))
            {
                stDup++;
                continue;
            }

            stIns++;
            if (s.Valid)
            {
                _robotState[(s.MapId, s.RobotId)] = new RobotStateRow(s.MapId, s.RobotId, s.To, s.Tick, s.EventId);
            }
        }

        foreach (var (c, log) in conflicts)
        {
            if (!_conflicts.TryAdd(c.EventId, log))
            {
                cfDup++;
                continue;
            }

            cfIns++;
        }

        return new StoreResult(telIns, telDup, taskIns, taskDup, stIns, stDup, cfIns, cfDup);
    }

    // ON CONFLICT (task_id) DO UPDATE SET col = COALESCE(EXCLUDED.col, task_history.col) WHERE EXCLUDED.status > task_history.status
    private void ApplyHistory(TaskHistoryChange change)
    {
        if (!_taskHistory.TryGetValue(change.TaskId, out var old))
        {
            _taskHistory[change.TaskId] = change;
            return;
        }

        if (change.Status <= old.Status)
        {
            return;
        }

        _taskHistory[change.TaskId] = old with
        {
            Status = change.Status,
            RobotId = change.RobotId ?? old.RobotId,
            TaskType = change.TaskType ?? old.TaskType,
            OriginX = change.OriginX ?? old.OriginX,
            OriginY = change.OriginY ?? old.OriginY,
            DestX = change.DestX ?? old.DestX,
            DestY = change.DestY ?? old.DestY,
            CreatedAt = change.CreatedAt ?? old.CreatedAt,
            AssignedAt = change.AssignedAt ?? old.AssignedAt,
            StartedAt = change.StartedAt ?? old.StartedAt,
            CompletedAt = change.CompletedAt ?? old.CompletedAt,
            PathLength = change.PathLength ?? old.PathLength,
            ActualSteps = change.ActualSteps ?? old.ActualSteps,
            WaitMs = change.WaitMs ?? old.WaitMs,
        };
    }

    private void AddMetrics(string mapId, string robotId, DateTimeOffset at, MetricsRow delta)
    {
        var key = (mapId, robotId, StoreMapping.MinuteOf(at));
        _metrics[key] = (_metrics.GetValueOrDefault(key) ?? new MetricsRow()).Add(delta);
    }
}

/// metrics_minute 한 줄의 값
public sealed record MetricsRow
{
    public long MoveMs { get; init; }

    public long WorkMs { get; init; }

    public long WaitMs { get; init; }

    public long IdleMs { get; init; }

    public long TaskCount { get; init; }

    public static MetricsRow Of(MetricsBucket bucket, int ms) => bucket switch
    {
        MetricsBucket.Move => new MetricsRow { MoveMs = ms },
        MetricsBucket.Work => new MetricsRow { WorkMs = ms },
        MetricsBucket.Wait => new MetricsRow { WaitMs = ms },
        _ => new MetricsRow { IdleMs = ms },
    };

    public MetricsRow Add(MetricsRow other) => new()
    {
        MoveMs = MoveMs + other.MoveMs,
        WorkMs = WorkMs + other.WorkMs,
        WaitMs = WaitMs + other.WaitMs,
        IdleMs = IdleMs + other.IdleMs,
        TaskCount = TaskCount + other.TaskCount,
    };
}

public sealed class InMemoryStoreException(string message) : Exception(message);
