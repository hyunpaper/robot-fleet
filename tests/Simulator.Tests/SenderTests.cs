using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using RobotFleet.Shared.Map;
using RobotFleet.Shared.Messaging;
using RobotFleet.Simulator.Events;
using RobotFleet.Simulator.Fleet;
using Xunit;

namespace RobotFleet.Simulator.Tests;

/// 송신기 중 인프라 없이 검증할 수 있는 것: S-04(버퍼가 차면 틱 정지 — 송신 루프를 돌리지 않는다), 송신 로그 형식.
/// 202 · 재전송 · 순차 송신(S-01 · S-02 · S-06)은 실제 Ingest로 Infrastructure.Tests의 SenderIntegrationTests에서
public sealed class SenderTests : IDisposable
{
    private static readonly UuidV7 Ids = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fleet-sender-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task S04_버퍼가_차면_틱_정지()
    {
        var map = MapLoader.LoadFromFile(Path.Combine(AppContext.BaseDirectory, "maps", "factory-a.json"));
        using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:9/") };   // 송신 루프를 돌리지 않으므로 쓰이지 않는다
        await using var sender = new EventBatchSender(new HttpIngestClient(http), null, new EventSenderOptions { Capacity = 2 },
            TimeProvider.System);
        var sim = new FleetSimulation(map, new FleetOptions { Robots = 3, Lambda = 0, MaxTicks = 10, SpeedFactor = 0 }, sender,
            new NoDelay(), null);
        using var cts = new CancellationTokenSource();
        var sw = Stopwatch.StartNew();
        var run = sim.RunAsync(cts.Token);

        while (sender.Buffered < 2 && sw.ElapsedMilliseconds < 5000) await Task.Delay(5);
        await Task.Delay(200);
        sender.Buffered.Should().Be(2);
        sim.Tick.Should().Be(3, "버퍼 2개가 찬 뒤 3번째 틱이 빈자리를 기다린다");
        run.IsCompleted.Should().BeFalse();

        await cts.CancelAsync();
        (await run).ExitCode.Should().Be(FleetSimulation.ExitCanceled);
    }

    [Fact]
    public void 송신_로그는_이벤트당_한_줄_형식()
    {
        var batch = new EventBatch(FleetSchema.Version, Ids.Next(), "m", "r1", 5, 100, DateTimeOffset.UnixEpoch,
        [
            new EventItem(EventType.TaskCreated, FleetJson.ToElement(new TaskCreatedPayload("T-1", 0, new CellDto(1, 1), new CellDto(2, 2), 0))),
            new EventItem(EventType.RobotTelemetry, FleetJson.ToElement(new RobotTelemetryPayload("R-1", 0, 0, 0, 0, 100, null, 0, 0))),
        ]);
        string path;
        using (var log = new SendLog(_dir, "m", "run1"))
        {
            log.Append(batch);
            path = log.Path;
        }

        var lines = File.ReadAllLines(path).Select(l => JsonDocument.Parse(l).RootElement).ToList();
        lines.Select(l => (l.GetProperty("eventId").GetString(), l.GetProperty("category").GetString(), l.GetProperty("key").GetString(),
                l.GetProperty("tick").GetInt64()))
            .Should().Equal(($"{batch.BatchId}/0", "task", "T-1", 5L), ($"{batch.BatchId}/1", "telemetry", "R-1", 5L));
    }
}
