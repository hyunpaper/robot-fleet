// 10 §12 R-run-04: Reconcile 차집합이 run 경계를 넘지 않는다. 실제 PostgreSQL(실행별 스키마), 접속 정보가 없으면 skip
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using RobotFleet.Infrastructure.Postgres;
using RobotFleet.Infrastructure.Postgres.Migrations;
using RobotFleet.Reconcile;
using RobotFleet.Shared.Messaging;
using RobotFleet.Shared.Pipeline;
using RobotFleet.TestSupport;

namespace RobotFleet.Infrastructure.Tests.Postgres;

[Trait("Category", "Integration")]
public sealed class ReconcileRunBoundaryTests : IAsyncLifetime
{
    private const string Map = "m-reconcile";
    private const string RunA = "20261013-090000-r1";
    private const string RunB = "20261013-100000-r1";
    private const string RunC = "20261013-110000-r1";
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-10-13T09:00:00Z");
    private PgSchemaFixture? _pg;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_pg is null) return;
        await using var admin = NpgsqlDataSource.Create(TestEnv.Pg!);
        await StaleResourceSweeper.SweepPostgresAsync(admin, DateTimeOffset.UtcNow, default);
        await _pg.DisposeAsync();
    }

    [IntegrationFact(TestEnv.PgVar)]
    public async Task R_run_04_차집합은_run_경계를_넘지_않는다()
    {
        var store = await StoreAsync();
        var created = FleetJson.ToElement(new TaskCreatedPayload("T-00001", 0, new CellDto(1, 1), new CellDto(2, 2), 0));

        // run A: tick 1~3 적재 완료. run B: 같은 로봇 · 같은 tick 중 1만 적재, 작업 이벤트는 미적재. run C: 전이 위반 1건
        await store.StoreBatchAsync(Batch(
            telemetry: [.. Enumerable.Range(1, 3).Select(t => Telemetry(RunA, t))],
            tasks: [new TaskEventRow("a/0", RunA, Map, "T-00001", EventType.TaskCreated, 1, At, created)]), default);
        await store.StoreBatchAsync(Batch(telemetry: [Telemetry(RunB, 1)]), default);
        await store.StoreBatchAsync(Batch(statuses: [new StatusChangeRow("c/0", RunC, Map, "R-001", 1, At, 0, 4, "unload_done", false)]),
            default);

        await using var conn = await _pg!.DataSource.OpenConnectionAsync();
        await SentEventImporter.ImportAsync(conn,
        [
            new SentEvent("a/0", "a", 1, EventType.TaskCreated, "task", "T-00001", RunA),
            new SentEvent("b/0", "b", 1, EventType.TaskCreated, "task", "T-00001", RunB),
            .. Enumerable.Range(1, 3).Select(t => new SentEvent($"b/t{t}", "b", t, EventType.RobotTelemetry, "telemetry", "R-001", RunB)),
        ], default);
        var queries = new ReconcileQueries();

        var task = await queries.TaskAsync(conn, default);
        (task.Sent, task.Stored, task.Missing).Should().Be((2L, 1L, 1L), "run A의 a/0은 적재됐고 run B의 b/0만 누락이다");
        task.MissingSample.Should().Equal("b/0");
        (await queries.TelemetryAsync(conn, default)).Missing.Should().Be(2, "run A의 tick 2 · 3을 run B의 적재로 착각하지 않는다");
        (await queries.InvalidTransitionsAsync(conn, default)).Should().Be(0, "sent에 없는 run C의 위반은 세지 않는다");
        (await queries.RunIdsAsync(conn, default)).Should().Equal(RunA, RunB);

        await queries.ComputeRunMetricsAsync(conn, RunA, default);
        await queries.ComputeRunMetricsAsync(conn, RunA, default);   // 재집계는 UPSERT
        (await _pg.ScalarAsync($"SELECT count(*) FROM run_metrics WHERE run_id = '{RunA}' AND move_ms = 300")).Should().Be(1);
    }

    private async Task<IEventStore> StoreAsync()
    {
        _pg = await PgSchemaFixture.CreateAsync();
        await new Migrator(_pg.DataSource, PgSchemaFixture.MigrationsDirectory, NullLogger<Migrator>.Instance).ApplyAsync(default);
        await new PartitionMaintainer(_pg.DataSource, TimeProvider.System, NullLogger<PartitionMaintainer>.Instance).EnsureAsync(default);
        return new PgEventStore(_pg.DataSource, NullLogger<PgEventStore>.Instance);
    }

    private static TelemetryRow Telemetry(string run, long tick) =>
        new(run, Map, "R-001", tick, 1, 1, 0, 1, 90, At.AddMilliseconds(tick), 100);

    private static StoreBatch Batch(IReadOnlyList<TelemetryRow>? telemetry = null, IReadOnlyList<TaskEventRow>? tasks = null,
        IReadOnlyList<StatusChangeRow>? statuses = null) =>
        new(telemetry ?? [], tasks ?? [], statuses ?? [], [], [], []);
}
