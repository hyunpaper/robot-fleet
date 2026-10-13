using Prometheus;
using RobotFleet.Shared.Messaging;

namespace RobotFleet.Simulator.Events;

/// 10 §11 Simulator 메트릭
public static class SimulatorMetrics
{
    public static readonly Counter EventsGenerated = Metrics.CreateCounter(
        "fleet_events_generated_total", "생성 이벤트 수", new CounterConfiguration { LabelNames = ["category"] });

    public static readonly Gauge BufferDepth = Metrics.CreateGauge("fleet_event_buffer_depth", "송신 버퍼 배치 수");

    public static readonly Counter SendRetry = Metrics.CreateCounter(
        "fleet_event_send_retry_total", "송신 재시도", new CounterConfiguration { LabelNames = ["reason"] });

    public static readonly Histogram TickStall = Metrics.CreateHistogram(
        "fleet_tick_stall_seconds", "틱 정지 시간", new HistogramConfiguration
        {
            LabelNames = ["reason"],
            Buckets = Histogram.ExponentialBuckets(0.01, 2, 14),
        });

    public static readonly Counter TickOverrun = Metrics.CreateCounter(
        "fleet_tick_overrun_total", "틱 초과", new CounterConfiguration { LabelNames = ["map_id"] });

    /// 회피 지점을 못 찾아 재시도 중인(장기 미해소) 교착 수. 작업은 실패시키지 않고 경보로 알린다(13 §5 · 12 등급 B)
    public static readonly Gauge DeadlockUnresolvedActive = Metrics.CreateGauge(
        "fleet_deadlock_unresolved_active", "장기 미해소 교착 수", new GaugeConfiguration { LabelNames = ["map_id"] });

    public static readonly Gauge DeadlockUnresolvedOldestSeconds = Metrics.CreateGauge(
        "fleet_deadlock_unresolved_oldest_seconds", "가장 오래된 미해소 교착 경과(초)", new GaugeConfiguration { LabelNames = ["map_id"] });

    internal static void CountGenerated(EventBatch batch)
    {
        foreach (var e in batch.Events)
            EventsGenerated.WithLabels(EventCategories.Of(e.EventType).ToString().ToLowerInvariant()).Inc();
    }
}
