using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using RobotFleet.Infrastructure.InMemory;
using RobotFleet.Shared.Map;
using RobotFleet.Shared.Messaging;
using RobotFleet.Shared.Pipeline;
using RobotFleet.Simulator.Events;
using RobotFleet.Simulator.Fleet;
using Xunit;

namespace RobotFleet.Simulator.Tests;

/// S-01~S-07 (10 §5 · §12)
public sealed class SenderTests : IDisposable
{
    private static readonly UuidV7 Ids = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fleet-sender-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static EventBatch Batch(long tick) =>
        new(FleetSchema.Version, Ids.Next(), "m", tick, 100, DateTimeOffset.UnixEpoch,
            [new EventItem(EventType.RobotTelemetry, FleetJson.ToElement(new RobotTelemetryPayload("R-1", 0, 0, 0, 0, 100, null, 0, 0)))]);

    private static IngestResult Unavailable => new(IngestStatus.Unavailable);

    private static async Task Until(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException();
            await Task.Delay(5);
        }
    }

    // 송신 로그는 쓰는 중에도 열려 있다(쓰기 공유 허용으로 읽는다)
    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return new StreamReader(stream).ReadToEnd();
    }

    private static EventSenderOptions Fast => new() { BackoffMin = TimeSpan.FromMilliseconds(1), BackoffMax = TimeSpan.FromMilliseconds(4) };

    [Fact]
    public async Task S01_머리_배치가_202를_받기_전에는_다음을_보내지_않는다()
    {
        var client = new FakeIngestClient();
        client.Script(Unavailable, Unavailable);
        var sender = new EventBatchSender(client, null, Fast, TimeProvider.System);
        var batches = new[] { Batch(1), Batch(2), Batch(3) };
        foreach (var b in batches) sender.TryWrite(b).Should().BeTrue();
        using var cts = new CancellationTokenSource();
        var run = sender.RunAsync(cts.Token);
        (await sender.DrainAsync(default)).Should().Be(0);
        await run;

        client.Sent.Select(s => s.Batch.Tick).Should().Equal(1, 1, 1, 2, 3);
    }

    [Fact]
    public async Task S02_재시도는_같은_바이트_같은_batchId()
    {
        var client = new FakeIngestClient();
        client.Script(Unavailable, Unavailable);
        var sender = new EventBatchSender(client, null, Fast, TimeProvider.System);
        sender.TryWrite(Batch(1));
        var run = sender.RunAsync(default);
        await sender.DrainAsync(default);
        await run;

        client.Sent.Should().HaveCount(3);
        client.Sent.Select(s => s.Batch.BatchId).Distinct().Should().ContainSingle();
        client.Sent.Select(s => Convert.ToBase64String(s.Body)).Distinct().Should().ContainSingle();
    }

    [Fact]
    public async Task S03_백오프는_100에서_두_배씩_2000_상한()
    {
        var time = new FakeTimeProvider();
        var client = new FakeIngestClient();
        client.Script(Enumerable.Repeat(Unavailable, 7).ToArray());
        var sender = new EventBatchSender(client, null, new EventSenderOptions(), time);
        sender.TryWrite(Batch(1));
        using var cts = new CancellationTokenSource();
        var run = sender.RunAsync(cts.Token);

        foreach (var expected in new[] { 100, 200, 400, 800, 1600, 2000, 2000 })
        {
            var before = client.Sent.Count;
            await Until(() => client.Sent.Count == before);
            await Task.Delay(20);
            time.Advance(TimeSpan.FromMilliseconds(expected - 1));
            await Task.Delay(20);
            client.Sent.Count.Should().Be(before, $"백오프 {expected}ms 전에는 재시도하지 않는다");
            time.Advance(TimeSpan.FromMilliseconds(1));
            await Until(() => client.Sent.Count == before + 1);
        }

        cts.Cancel();
        await run;
    }

    [Fact]
    public async Task S04_버퍼가_차면_틱_정지_비면_재개()
    {
        var map = MapLoader.LoadFromFile(Path.Combine(AppContext.BaseDirectory, "maps", "factory-a.json"));
        var client = new FakeIngestClient();
        var sender = new EventBatchSender(client, null, new EventSenderOptions { Capacity = 2 }, TimeProvider.System);
        var sim = new FleetSimulation(map, new FleetOptions { Robots = 3, Lambda = 0, MaxTicks = 10, SpeedFactor = 0 }, sender, new NoDelay(), null);
        var simRun = sim.RunAsync(default);

        await Until(() => sender.Buffered == 2);
        await Task.Delay(100);
        sim.Tick.Should().Be(3, "버퍼 2개가 찬 뒤 3번째 틱이 송신을 기다린다");
        simRun.IsCompleted.Should().BeFalse();

        var run = sender.RunAsync(default);
        var result = await simRun;
        result.Ticks.Should().Be(10);
        result.StallTime.Should().BeGreaterThan(TimeSpan.FromMilliseconds(50));
        (await sender.DrainAsync(default)).Should().Be(0);
        await run;
    }

    [Fact]
    public async Task S05_400이면_송신_정지_종료_코드_4_덤프()
    {
        var map = MapLoader.LoadFromFile(Path.Combine(AppContext.BaseDirectory, "maps", "factory-a.json"));
        var client = new FakeIngestClient();
        client.Script(new IngestResult(IngestStatus.Invalid, Error: "bad"));
        var sender = new EventBatchSender(client, null, new EventSenderOptions { Capacity = 2, FailureDumpDirectory = _dir }, TimeProvider.System);
        var run = sender.RunAsync(default);
        var result = await new FleetSimulation(map, new FleetOptions { Robots = 2, Lambda = 0, MaxTicks = 1000, SpeedFactor = 0 },
            sender, new NoDelay(), null).RunAsync(default);
        await run;

        result.ExitCode.Should().Be(FleetSimulation.ExitSinkClosed);
        sender.Faulted.Should().BeTrue();
        sender.FaultReason.Should().Contain("400");
        Directory.GetFiles(_dir, "rejected-*.json").Should().ContainSingle();
        client.Sent.Should().ContainSingle();
    }

    [Fact]
    public async Task S06_송신_로그는_202_뒤에만_못_보낸_배치는_unsent()
    {
        var client = new FakeIngestClient { Delay = TimeSpan.FromMilliseconds(200) };
        client.Script(Unavailable);
        using var log = new SendLog(_dir, "m", "run1");
        var sender = new EventBatchSender(client, log, Fast with { DrainTimeout = TimeSpan.FromSeconds(10) }, TimeProvider.System);
        var batch = Batch(1);
        sender.TryWrite(batch);
        var run = sender.RunAsync(default);

        await Until(() => client.Sent.Count == 1);
        ReadShared(log.Path).Should().BeEmpty("202를 받기 전");
        await sender.DrainAsync(default);
        await run;
        ReadShared(log.Path).Split('\n', StringSplitOptions.RemoveEmptyEntries).Should().ContainSingle().Which.Should().Contain($"\"eventId\":\"{batch.BatchId}/0\"")
            .And.Contain("\"category\":\"telemetry\"").And.Contain("\"key\":\"R-1\"");

        var dead = new FakeIngestClient();
        dead.Script(Enumerable.Repeat(Unavailable, 1000).ToArray());
        var stuck = new EventBatchSender(dead, null, Fast with { DrainTimeout = TimeSpan.FromMilliseconds(100) }, TimeProvider.System);
        stuck.TryWrite(Batch(1));
        stuck.TryWrite(Batch(2));
        using var cts = new CancellationTokenSource();
        var stuckRun = stuck.RunAsync(cts.Token);
        (await stuck.DrainAsync(default)).Should().Be(2);
        cts.Cancel();
        await stuckRun;
    }

    [Fact]
    public async Task S07_틱_루프는_네트워크를_기다리지_않는다()
    {
        var map = MapLoader.LoadFromFile(Path.Combine(AppContext.BaseDirectory, "maps", "factory-a.json"));
        var client = new FakeIngestClient { Delay = TimeSpan.FromSeconds(1) };
        var sender = new EventBatchSender(client, null, new EventSenderOptions(), TimeProvider.System);
        using var cts = new CancellationTokenSource();
        var run = sender.RunAsync(cts.Token);

        var sw = Stopwatch.StartNew();
        var result = await new FleetSimulation(map, new FleetOptions { Robots = 20, Lambda = 1, MaxTicks = 200, SpeedFactor = 0 },
            sender, new NoDelay(), null).RunAsync(default);
        sw.Stop();

        result.Ticks.Should().Be(200);
        client.Sent.Count.Should().BeLessThan(5, "느린 송신 중에도 200틱이 진행됐다");
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
        cts.Cancel();
        await run;
    }
}
