// 10 §12 S-01 · S-02 · S-06(송신기) · 완료 기준 1~3(유실 0 · 중복 0 · 순서): Simulator → HTTP → IngestApi → RabbitMQ → 워커 → PostgreSQL,
// Redis 멱등 포함. 접속 정보 셋이 모두 있을 때만
using System.Diagnostics;
using FluentAssertions;
using Npgsql;
using RobotFleet.Reconcile;
using RobotFleet.Shared.Map;
using RobotFleet.Simulator;
using RobotFleet.Simulator.Events;
using RobotFleet.Simulator.Fleet;
using RobotFleet.TestSupport;

namespace RobotFleet.Infrastructure.Tests.Pipeline;

[Trait("Category", "Integration")]
public sealed class EndToEndTests : PipelineTestBase, IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fleet-e2e-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [IntegrationFact(TestEnv.RabbitMqVar, TestEnv.RedisVar, TestEnv.PgVar)]
    public async Task S01_S02_S06_503이면_같은_batchId로_순차_재전송_로그는_202_뒤에만()
    {
        var rig = await RigAsync(redis: true, postgres: false);
        var baseUri = await rig.StartIngestHostAsync();
        var first = Batch(1, Created("T-1"));
        var second = Batch(2, Created("T-2"));
        await rig.Idem!.TryBeginAsync(first.BatchId, default);   // 다른 인스턴스가 발행 중 → 5초 동안 503, 이후 고아 재점유

        using var http = new HttpClient { BaseAddress = baseUri };
        using var log = new SendLog(_dir, Map, "s");
        await using var sender = new EventBatchSender(new HttpIngestClient(http), log, new EventSenderOptions(), TimeProvider.System);
        sender.TryWrite(first);
        sender.TryWrite(second);
        var sw = Stopwatch.StartNew();
        var run = sender.RunAsync(default);

        await Task.Delay(1500);
        ReadShared(log.Path).Should().BeEmpty("첫 배치가 202를 받기 전에는 아무것도 기록하지 않고 두 번째도 보내지 않는다");
        (await sender.DrainAsync(default)).Should().Be(0);
        await run;

        sw.Elapsed.Should().BeGreaterThan(TimeSpan.FromSeconds(5));
        var lines = ReadShared(log.Path).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(2);
        lines[0].Should().Contain(first.BatchId);
        lines[1].Should().Contain(second.BatchId);
        await PipelineRig.UntilAsync(async () => await rig.ReadyAsync() == 2);   // 재전송이 중복을 만들지 않았다
    }

    [IntegrationFact(TestEnv.RabbitMqVar, TestEnv.RedisVar, TestEnv.PgVar)]
    public async Task 완료_기준_1_2_3_로봇_20대_워커_kill_1회_차집합_0()
    {
        var rig = await RigAsync(redis: true, postgres: true);
        var baseUri = await rig.StartIngestHostAsync();
        var map = MapLoader.LoadFromFile(Path.Combine(AppContext.BaseDirectory, "maps", "factory-a.json"));

        using var http = new HttpClient { BaseAddress = baseUri };
        string logPath;
        using (var log = new SendLog(_dir, map.MapId, "e2e"))
        {
            logPath = log.Path;
            await using var sender = new EventBatchSender(new HttpIngestClient(http), log, new EventSenderOptions(), TimeProvider.System);
            var sendLoop = sender.RunAsync(default);
            var a = rig.StartWorker("w-a");
            var sim = new FleetSimulation(map, new FleetOptions { Robots = 20, Lambda = 1, Seed = 5, MaxTicks = 600, SpeedFactor = 0 },
                sender, new NoDelay(), null);
            sim.AfterTick = async (tick, _) =>
            {
                if (tick == 300)
                {
                    await rig.KillAsync(a);   // 처리 중 사망 → 미확인분 반환 → 다음 워커가 재처리
                    rig.StartWorker("w-b");
                }
            };

            var result = await sim.RunAsync(default);
            result.Ticks.Should().Be(600);
            (await sender.DrainAsync(default)).Should().Be(0);
            await sendLoop;
        }

        await rig.DrainedAsync(120_000);

        // 송신 로그(202 뒤 기록) − 적재 집합: src/Reconcile 과 같은 질의
        await using var conn = await rig.Pg!.DataSource.OpenConnectionAsync();
        await SentEventImporter.ImportAsync(conn, SendLogReader.Read(logPath), default);
        var queries = new ReconcileQueries();
        var task = await queries.TaskAsync(conn, default);
        var conflict = await queries.ConflictAsync(conn, default);
        var telemetry = await queries.TelemetryAsync(conn, default);

        task.Sent.Should().BeGreaterThan(0);
        task.Missing.Should().Be(0, string.Join(",", task.MissingSample));
        conflict.Missing.Should().Be(0, string.Join(",", conflict.MissingSample));
        telemetry.Missing.Should().Be(0);
        (await queries.InvalidTransitionsAsync(conn, default)).Should().Be(0);
        var runs = await queries.RunIdsAsync(conn, default);
        runs.Should().ContainSingle();
        await queries.ComputeRunMetricsAsync(conn, runs[0], default);
        (await rig.ScalarAsync("SELECT count(*) FROM run_metrics WHERE utilization IS NOT NULL")).Should().Be(1);
        (await rig.ScalarAsync("SELECT count(*) FROM simulations WHERE started_at IS NOT NULL AND ended_at IS NOT NULL")).Should().Be(1);
        await using var dup = new NpgsqlCommand("SELECT count(*) - count(DISTINCT task_id) FROM task_history", conn);
        ((long)(await dup.ExecuteScalarAsync())!).Should().Be(0);
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return new StreamReader(stream).ReadToEnd();
    }
}
