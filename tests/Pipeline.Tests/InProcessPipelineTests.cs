using RobotFleet.Infrastructure.InMemory;
using RobotFleet.PipelineHost;
using RobotFleet.Shared.Map;
using RobotFleet.Shared.Messaging;
using RobotFleet.Simulator.Fleet;
using Xunit.Abstractions;

namespace RobotFleet.Pipeline.Tests;

/// P-01 · P-02 (10 §12): 송신 집합(202) − 적재 집합 = 0
public sealed class InProcessPipelineTests(ITestOutputHelper output)
{
    private static readonly GridMap Factory = MapLoader.LoadFromFile(Path.Combine(AppContext.BaseDirectory, "maps", "factory-a.json"));

    private static FleetOptions Fleet => new() { Robots = 20, Lambda = 1, Seed = 11, MaxTicks = 2000, SpeedFactor = 0 };

    [Fact]
    public async Task P01_로봇_20대_2000틱_차집합_0_invalid_0()
    {
        await using var pipeline = new InProcessPipeline(Factory, Fleet);
        pipeline.StartWorker("w-0");
        var report = await pipeline.RunAsync(TimeSpan.FromSeconds(60));
        output.WriteLine(report.ToString());

        report.Simulation.Ticks.Should().Be(2000);
        report.TaskSent.Should().BeGreaterThan(100);
        report.ConflictSent.Should().BeGreaterThan(0);
        report.Missing.Should().BeEmpty();
        report.TelemetryMissing.Should().Be(0);
        report.InvalidTransitions.Should().Be(0);
        report.LossFree.Should().BeTrue();
    }

    [Fact]
    public async Task P02_워커_kill_2회_발행_장애_저장_장애를_섞어도_차집합_0()
    {
        await using var pipeline = new InProcessPipeline(Factory, Fleet, sharedRoster: true);
        pipeline.StartWorker("w-a");
        pipeline.StartWorker("w-b");
        var broker = pipeline.Broker;
        pipeline.Simulation.AfterTick = async (tick, _) =>
        {
            switch (tick)
            {
                case 300:
                    broker.InjectPublishFault(PublishFault.Nack, 3, EventCategory.Task);
                    break;
                case 500:
                    await pipeline.KillWorkerAsync("w-a");   // ack 전 미확인분 반환
                    pipeline.StartWorker("w-c");
                    break;
                case 800:
                    broker.InjectPublishFault(PublishFault.ConfirmTimeout, 3, EventCategory.Conflict);   // 들어갔는데 503 → 재전송 중복
                    broker.InjectPublishFault(PublishFault.ChannelClosed, 1, EventCategory.Task);
                    break;
                case 1000:
                    pipeline.Store.FailNext(2);
                    break;
                case 1200:
                    await pipeline.KillWorkerAsync("w-b");
                    pipeline.StartWorker("w-d");
                    break;
                case 1500:
                    pipeline.Store.FailAfterCommitNext(1);   // 커밋됐는데 실패로 보임 → 재처리 중복
                    break;
            }

            if (tick % 100 == 0) await Task.Delay(10);   // 워커가 따라오게 틈을 준다
        };

        var report = await pipeline.RunAsync(TimeSpan.FromSeconds(60));
        output.WriteLine(report.ToString());

        report.Simulation.Ticks.Should().Be(2000);
        report.Unsent.Should().Be(0);
        report.Missing.Should().BeEmpty();
        report.Backlog.Should().Be(0);
        report.TelemetryMissing.Should().Be(0);
        report.InvalidTransitions.Should().Be(0);
        report.DeadLettered.Should().Be(0);
        report.Returned.Should().BeGreaterThan(0, "kill이 미확인 메시지를 반환시켜 재처리가 일어났다");
        report.LossFree.Should().BeTrue();
    }
}
