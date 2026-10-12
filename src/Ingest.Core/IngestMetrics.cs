using Prometheus;

namespace RobotFleet.Ingest;

/// 10 §11 IngestApi 메트릭
public static class IngestMetrics
{
    public static readonly Counter EventsReceived = Metrics.CreateCounter(
        "fleet_events_received_total", "수신 이벤트 수", new CounterConfiguration { LabelNames = ["event_type"] });

    public static readonly Counter Batches = Metrics.CreateCounter(
        "fleet_ingest_batches_total", "배치 처리 결과", new CounterConfiguration { LabelNames = ["outcome"] });

    public static readonly Histogram PublishSeconds = Metrics.CreateHistogram(
        "fleet_ingest_publish_seconds", "배치 발행 · 확인 대기 시간",
        new HistogramConfiguration { Buckets = Histogram.ExponentialBuckets(0.001, 2, 12) });

    public static readonly Counter TelemetryDropped = Metrics.CreateCounter(
        "fleet_telemetry_dropped_total", "버린 telemetry 수", new CounterConfiguration { LabelNames = ["reason"] });

    public static readonly Counter IdemBypass = Metrics.CreateCounter(
        "fleet_ingest_idem_bypass_total", "멱등 저장소 불통으로 1단계를 생략한 배치");

    public static readonly Counter IdemOrphanTakeover = Metrics.CreateCounter(
        "fleet_ingest_idem_orphan_takeover_total", "고아 pending 재점유");
}
