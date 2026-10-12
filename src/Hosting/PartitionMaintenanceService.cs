using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Prometheus;
using RobotFleet.Infrastructure.Postgres.Migrations;
using RobotFleet.Shared.Pipeline;

namespace RobotFleet.Hosting;

/// postgres.migrations가 Ready가 된 뒤 시작, 매시 정각 파티션 유지 + DEFAULT 파티션 행 수 게이지. 실패는 로그만
public sealed class PartitionMaintenanceService(
    PartitionMaintainer maintainer,
    ReadinessState readiness,
    TimeProvider time,
    ILogger<PartitionMaintenanceService> logger) : BackgroundService
{
    private static readonly Gauge DefaultRows = Metrics.CreateGauge(
        "fleet_telemetry_default_rows", "텔레메트리 DEFAULT 파티션 행 수");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!readiness.Snapshot().GetValueOrDefault(ReadinessComponents.PostgresMigrations))
                await Task.Delay(TimeSpan.FromSeconds(1), time, stoppingToken);

            while (true)
            {
                await Task.Delay(UntilNextHour(), time, stoppingToken);
                await RunOnceAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private TimeSpan UntilNextHour()
    {
        var now = time.GetUtcNow();
        var next = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero).AddHours(1);
        return next - now;
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            await maintainer.EnsureAsync(ct);
            DefaultRows.Set(await maintainer.CountDefaultRowsAsync(ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "파티션 유지 실패, 다음 정각에 재시도");
        }
    }
}
