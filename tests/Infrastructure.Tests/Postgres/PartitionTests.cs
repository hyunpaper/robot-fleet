// 10 §12 D-03
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using RobotFleet.Infrastructure.Postgres.Migrations;
using RobotFleet.TestSupport;

namespace RobotFleet.Infrastructure.Tests.Postgres;

[Trait("Category", "Integration")]
public sealed class PartitionTests
{
    [IntegrationFact("FLEET_TEST_PG")]
    [Trait("Category", "Integration")]
    public async Task D03_당일_익일_생성_범위_밖은_DEFAULT_메트릭()
    {
        await using var pg = await PgSchemaFixture.CreateAsync();
        await new Migrator(pg.DataSource, PgSchemaFixture.MigrationsDirectory, NullLogger<Migrator>.Instance)
            .ApplyAsync(CancellationToken.None);

        // UTC 날짜 경계 직전
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 12, 23, 30, 0, TimeSpan.Zero));
        var maintainer = new PartitionMaintainer(pg.DataSource, time, NullLogger<PartitionMaintainer>.Instance);

        await maintainer.EnsureAsync(CancellationToken.None);
        await maintainer.EnsureAsync(CancellationToken.None);

        (await pg.ScalarAsync(
            "SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace " +
            $"WHERE n.nspname = '{pg.Schema}' AND c.relname IN ('robot_telemetry_20261012', 'robot_telemetry_20261013')"))
            .Should().Be(2);

        await Insert(pg, 1, "2026-10-12T23:59:59Z");
        await Insert(pg, 2, "2026-10-13T00:00:00Z");
        (await PartitionOf(pg, 1)).Should().Be("robot_telemetry_20261012");
        (await PartitionOf(pg, 2)).Should().Be("robot_telemetry_20261013");

        (await maintainer.CountDefaultRowsAsync(CancellationToken.None)).Should().Be(0);
        await Insert(pg, 3, "2020-01-01T00:00:00Z");
        (await PartitionOf(pg, 3)).Should().Be("robot_telemetry_default");
        (await maintainer.CountDefaultRowsAsync(CancellationToken.None)).Should().Be(1);
    }

    private static async Task Insert(PgSchemaFixture pg, long tick, string occurredAt)
    {
        await using var cmd = pg.DataSource.CreateCommand(
            "INSERT INTO robot_telemetry (run_id, map_code, robot_id, tick, x, y, heading, status, occurred_at) " +
            $"VALUES ('20261012-000000-r1', 'm', 'r', {tick}, 0, 0, 0, 0, '{occurredAt}')");
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<string> PartitionOf(PgSchemaFixture pg, long tick)
    {
        await using var cmd = pg.DataSource.CreateCommand(
            "SELECT c.relname FROM robot_telemetry t JOIN pg_class c ON c.oid = t.tableoid " +
            $"WHERE t.tick = {tick}");
        return (string)(await cmd.ExecuteScalarAsync())!;
    }
}
