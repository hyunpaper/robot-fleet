// 10 §8, §13
using Microsoft.Extensions.Logging;
using Npgsql;

namespace RobotFleet.Infrastructure.Postgres.Migrations;

public sealed class PartitionMaintainer(NpgsqlDataSource dataSource, TimeProvider time, ILogger<PartitionMaintainer> logger)
{
    /// UTC 당일 · 익일 파티션을 만든다(멱등)
    public async Task EnsureAsync(CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        foreach (var day in new[] { today, today.AddDays(1) })
        {
            await using var cmd = dataSource.CreateCommand("SELECT fleet_ensure_telemetry_partition(@day)");
            cmd.Parameters.Add(new NpgsqlParameter<DateOnly>("day", day));
            var name = (string?)await cmd.ExecuteScalarAsync(ct);
            logger.LogDebug("텔레메트리 파티션 확인: {Partition}", name);
        }
    }

    public async Task<long> CountDefaultRowsAsync(CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("SELECT count(*) FROM robot_telemetry_default");
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// 시작 즉시 1회, 이후 매시 정각 EnsureAsync + CountDefaultRows. 취소될 때까지 돈다
    public async Task RunHourlyAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await EnsureAsync(ct);
                var rows = await CountDefaultRowsAsync(ct);
                if (rows != 0)
                {
                    logger.LogWarning("DEFAULT 파티션에 행 {Rows}건: 시계 오차 가능, 수동 이동 필요", rows);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "파티션 유지 실패, 다음 정각에 재시도");
            }

            var now = time.GetUtcNow();
            var nextHour = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero).AddHours(1);
            try
            {
                await Task.Delay(nextHour - now, time, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
