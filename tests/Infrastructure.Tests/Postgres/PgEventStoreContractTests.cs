// 10 §12 C-03 · D-04: EventStore 계약을 실제 PostgreSQL(실행별 스키마)에서. 접속 정보가 없으면 skip
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using RobotFleet.Infrastructure.Postgres;
using RobotFleet.Infrastructure.Postgres.Migrations;
using RobotFleet.Shared.Pipeline;
using RobotFleet.TestSupport;
using RobotFleet.Infrastructure.Tests.Contracts;

namespace RobotFleet.Infrastructure.Tests.Postgres;

[Trait("Category", "Integration")]
public sealed class PgEventStoreContractTests : EventStoreContract, IAsyncLifetime
{
    private PgSchemaFixture? _pg;

    protected override async Task<IEventStore> CreateAsync()
    {
        _pg ??= await PgSchemaFixture.CreateAsync();
        await new Migrator(_pg.DataSource, PgSchemaFixture.MigrationsDirectory, NullLogger<Migrator>.Instance).ApplyAsync(default);
        await new PartitionMaintainer(_pg.DataSource, TimeProvider.System, NullLogger<PartitionMaintainer>.Instance).EnsureAsync(default);
        return new PgEventStore(_pg.DataSource, NullLogger<PgEventStore>.Instance);
    }

    protected override async Task<(long MoveMs, long WaitMs, long TaskCount)> ReadMetricsAsync(string robotId, DateTimeOffset minute)
    {
        await using var cmd = _pg!.DataSource.CreateCommand(
            "SELECT move_ms, wait_ms, task_count FROM metrics_minute WHERE map_id = @m AND robot_id = @r AND minute = @t");
        cmd.Parameters.AddWithValue("m", Map);
        cmd.Parameters.AddWithValue("r", robotId);
        cmd.Parameters.AddWithValue("t", StoreMapping.MinuteOf(minute));
        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2)) : (0, 0, 0);
    }

    protected override async Task<(short Status, string? RobotId)?> ReadTaskAsync(string taskId)
    {
        await using var cmd = _pg!.DataSource.CreateCommand("SELECT status, robot_id FROM task_history WHERE task_id = @t");
        cmd.Parameters.AddWithValue("t", taskId);
        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? (reader.GetInt16(0), reader.IsDBNull(1) ? null : reader.GetString(1)) : null;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_pg is null) return;
        await using var admin = NpgsqlDataSource.Create(TestEnv.Pg!);
        await StaleResourceSweeper.SweepPostgresAsync(admin, DateTimeOffset.UtcNow, default);
        await _pg.DisposeAsync();
    }

    [IntegrationFact(TestEnv.PgVar)] public Task C03_W10_telemetry_중복은_집계_1회() => Duplicate_telemetry_is_counted_once_in_metrics();
    [IntegrationFact(TestEnv.PgVar)] public Task C03_W11_TaskCompleted_중복은_task_count_1회() => Duplicate_task_completed_counts_once();
    [IntegrationFact(TestEnv.PgVar)] public Task C03_W12_task_history_단조() => Task_history_is_monotonic();
    [IntegrationFact(TestEnv.PgVar)] public Task C03_위반_영수증과_상태() => Status_receipts_keep_invalid_and_state_follows_valid_only();
    [IntegrationFact(TestEnv.PgVar)] public Task C03_conflict_중복() => Duplicate_conflict_is_ignored();

    [IntegrationFact(TestEnv.PgVar)]
    public async Task D04_unnest_1000행_RETURNING은_새_행만()
    {
        var store = await CreateAsync();
        var rows = Enumerable.Range(0, 1000).Select(i => Telemetry($"r-{i % 50}", tick: i / 50, status: 1)).ToList();
        var first = await store.StoreBatchAsync(Batch(telemetry: rows), default);
        var mixed = await store.StoreBatchAsync(Batch(telemetry: [.. rows.Take(500), Telemetry("r-new", 999, 0)]), default);

        first.TelemetryInserted.Should().Be(1000);
        mixed.TelemetryInserted.Should().Be(1);
        mixed.TelemetryDuplicate.Should().Be(500);
        (await _pg!.ScalarAsync("SELECT count(*) FROM robot_telemetry")).Should().Be(1001);
    }
}
