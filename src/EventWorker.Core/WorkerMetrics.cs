using Prometheus;
using RobotFleet.Shared.Messaging;
using RobotFleet.Shared.Pipeline;

namespace RobotFleet.EventWorker;

/// 10 §11 EventWorker 메트릭
public static class WorkerMetrics
{
    public static readonly Counter Messages = Metrics.CreateCounter(
        "fleet_worker_messages_total", "워커 메시지 처리 결과",
        new CounterConfiguration { LabelNames = ["category", "outcome"] });

    public static readonly Histogram BatchSize = Metrics.CreateHistogram(
        "fleet_worker_batch_size", "파티션 배치 크기",
        new HistogramConfiguration { Buckets = [1, 2, 5, 10, 20, 50, 100] });

    public static readonly Histogram BatchSeconds = Metrics.CreateHistogram(
        "fleet_worker_batch_seconds", "배치 처리(저장 · ack) 시간",
        new HistogramConfiguration { Buckets = Histogram.ExponentialBuckets(0.001, 2, 14) });

    public static readonly Histogram EventLag = Metrics.CreateHistogram(
        "fleet_event_lag_seconds", "occurredAt → 커밋",
        new HistogramConfiguration { Buckets = Histogram.ExponentialBuckets(0.01, 2, 12) });

    public static readonly Gauge PartitionsOwned = Metrics.CreateGauge(
        "fleet_worker_partitions_owned", "소유 파티션 번호 수");

    public static readonly Counter Reassign = Metrics.CreateCounter(
        "fleet_worker_reassign_total", "몫 전환 횟수");

    public static readonly Counter DbRetry = Metrics.CreateCounter(
        "fleet_worker_db_retry_total", "저장 일시 오류 재시도");

    public static readonly Gauge QueueDepth = Metrics.CreateGauge(
        "fleet_queue_depth", "큐 대기 메시지 수", new GaugeConfiguration { LabelNames = ["queue"] });

    internal static void Record(IReadOnlyList<Delivery> batch, StoreResult r, int invalid, int poison, int skipped)
    {
        Messages.WithLabels("telemetry", "stored").Inc(r.TelemetryInserted);
        Messages.WithLabels("telemetry", "duplicate").Inc(r.TelemetryDuplicate);
        Messages.WithLabels("task", "stored").Inc(r.TaskInserted + r.StatusInserted);
        Messages.WithLabels("task", "duplicate").Inc(r.TaskDuplicate + r.StatusDuplicate);
        Messages.WithLabels("task", "invalid").Inc(invalid);
        Messages.WithLabels("conflict", "stored").Inc(r.ConflictInserted);
        Messages.WithLabels("conflict", "duplicate").Inc(r.ConflictDuplicate);
        Messages.WithLabels("conflict", "skipped").Inc(skipped);
        Messages.WithLabels("any", "dead_lettered").Inc(poison);
        BatchSize.Observe(batch.Count);
    }

    internal static string CategoryOf(string queue) =>
        queue.Contains(".telemetry.", StringComparison.Ordinal) ? nameof(EventCategory.Telemetry)
        : queue.Contains(".task.", StringComparison.Ordinal) ? nameof(EventCategory.Task)
        : nameof(EventCategory.Conflict);
}
