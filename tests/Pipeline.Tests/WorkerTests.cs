using System.Text;
using RobotFleet.Infrastructure.InMemory;
using RobotFleet.Shared.Messaging;
using RobotFleet.Shared.Pipeline;
using static RobotFleet.Pipeline.Tests.TestBatches;

namespace RobotFleet.Pipeline.Tests;

/// W-01~W-09 (10 §12). W-10~W-12는 EventStore 계약(InMemoryEventStoreContractTests)
public sealed class WorkerTests
{
    private readonly WorkerRig _rig = new();

    private static async Task<T> RunningAsync<T>(Func<CancellationToken, Task> run, Func<Task<T>> body)
    {
        using var cts = new CancellationTokenSource();
        var task = run(cts.Token);
        try
        {
            return await body();
        }
        finally
        {
            cts.Cancel();
            await task;
        }
    }

    [Fact]
    public async Task W01_배치는_최대_50건_창으로_묶인다()
    {
        for (var t = 0; t < 4; t++)
            await _rig.SendAsync(Batch(t, Enumerable.Range(0, 40).Select(_ => Telemetry("R-1")).ToArray()));
        var worker = _rig.Worker(WorkerRig.Single());
        await RunningAsync(worker.RunAsync, async () =>
        {
            await WorkerRig.Until(() => _rig.Store.TelemetryCount == 4);   // (R-1, tick) 자연 키라 배치마다 1행
            await _rig.DrainedAsync();
            return 0;
        });

        _rig.Recording.BatchSizes.Should().OnlyContain(n => n <= 50);
        _rig.Recording.BatchSizes.Sum().Should().Be(160);
        _rig.Recording.BatchSizes.Should().Contain(n => n > 1);
    }

    [Fact]
    public async Task W02_ack는_커밋_뒤에만()
    {
        await _rig.SendAsync(Batch(1, Created("T-1"), Created("T-1")));
        var queue = _rig.Broker.QueueFor(EventCategory.Task, "T-1");
        var hold = new TaskCompletionSource();
        _rig.Recording.HoldBefore = hold;
        var worker = _rig.Worker(WorkerRig.Single());
        await RunningAsync(worker.RunAsync, async () =>
        {
            await _rig.Recording.Entered.Task;
            await Task.Delay(100);
            _rig.Broker.UnackedCount(queue).Should().Be(2);   // 저장 중: 아직 미확인
            _rig.Store.ReceiptIds().Should().BeEmpty();

            hold.SetResult();
            await WorkerRig.Until(() => _rig.Broker.UnackedCount(queue) == 0);
            _rig.Store.ReceiptIds().Should().HaveCount(2);
            return 0;
        });
    }

    [Fact]
    public async Task W03_저장_일시_오류는_ack_nack_없이_재시도_전달_횟수_불변()
    {
        _rig.Store.FailNext(3);
        await _rig.SendAsync(Batch(1, Created("T-1")));
        var worker = _rig.Worker(WorkerRig.Single());
        await RunningAsync(worker.RunAsync, async () =>
        {
            await WorkerRig.Until(() => _rig.Store.ReceiptIds().Count == 1);
            await _rig.DrainedAsync();
            return 0;
        });

        _rig.Broker.Returned.Should().Be(0);   // 반환(재전달) 없이 같은 배치를 다시 저장했다
        _rig.Broker.DeadLetters().Should().BeEmpty();
    }

    [Fact]
    public async Task W04_커밋_후_예외면_재처리해도_중복_0()
    {
        _rig.Store.FailAfterCommitNext(1);
        await _rig.SendAsync(Batch(1, Created("T-1"), Status("R-1", 0, 1, "task_assigned"), Telemetry("R-1")));
        var worker = _rig.Worker(WorkerRig.Single());
        await RunningAsync(worker.RunAsync, async () =>
        {
            await WorkerRig.Until(() => _rig.Store.ReceiptIds().Count == 2 && _rig.Store.TelemetryCount == 1);
            await _rig.DrainedAsync();
            return 0;
        });

        _rig.Store.StatusLog().Should().ContainSingle().Which.Valid.Should().BeTrue();
        _rig.Broker.InvalidMessages().Should().BeEmpty();
    }

    [Fact]
    public async Task W05_워커_kill_반환_재처리해도_행_집계_1회()
    {
        var events = Enumerable.Range(0, 30).Select(i => Created($"T-{i}")).ToArray();
        await _rig.SendAsync(Batch(1, events));
        var hold = new TaskCompletionSource();
        _rig.Recording.HoldAfter = hold;   // 첫 배치를 커밋한 뒤 ack 전에 멈춘다

        var a = _rig.Worker(WorkerRig.Single(), WorkerRig.Options("w-a"));
        using (var cts = new CancellationTokenSource())
        {
            var run = a.RunAsync(cts.Token);
            await _rig.Recording.Entered.Task;
            await WorkerRig.Until(() => !_rig.Recording.BatchSizes.IsEmpty);   // 커밋됨, ack 전
            await a.KillAsync();                                                // 프로세스 죽음
            _rig.Recording.HoldAfter = null;
            hold.SetResult();
            cts.Cancel();
            await run;
        }

        _rig.Broker.Returned.Should().BeGreaterThan(0);
        var b = _rig.Worker(WorkerRig.Single(), WorkerRig.Options("w-b"));
        await RunningAsync(b.RunAsync, async () =>
        {
            await _rig.DrainedAsync();
            return 0;
        });

        _rig.Store.ReceiptIds().Should().HaveCount(30);
        _rig.Recording.BatchSizes.Sum().Should().BeGreaterThan(30);   // 재처리가 있었다
        for (var i = 0; i < 30; i++)
            _rig.Store.TaskHistory($"T-{i}")!.Status.Should().Be(0);
    }

    [Fact]
    public async Task W06_역직렬화_실패는_그_건만_DLQ()
    {
        var good = Batch(1, Created("T-1"));
        await _rig.SendAsync(good);
        await _rig.Broker.PublishBatchAsync(
            [new OutgoingMessage(EventCategory.Task, "T-1", "bad/0", EventType.TaskCreated, true, Encoding.UTF8.GetBytes("{oops"))], default);
        await _rig.SendAsync(Batch(2, Created("T-1")));

        var worker = _rig.Worker(WorkerRig.Single());
        await RunningAsync(worker.RunAsync, async () =>
        {
            await _rig.DrainedAsync();
            return 0;
        });

        _rig.Broker.DeadLetters().Select(m => m.MessageId).Should().Equal("bad/0");
        _rig.Store.ReceiptIds().Should().HaveCount(2);
    }

    [Fact]
    public async Task W07_21번_반환되면_DLQ()
    {
        await _rig.SendAsync(Batch(1, Created("T-1")));
        var queue = _rig.Broker.QueueFor(EventCategory.Task, "T-1");
        for (var i = 0; i < 21; i++)
        {
            var got = new TaskCompletionSource();
            var sub = await _rig.Broker.SubscribeAsync(queue, 50, (_, _) => { got.TrySetResult(); return ValueTask.CompletedTask; }, default);
            await got.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await sub.DisposeAsync();   // ack 없이 채널 닫기 = 크래시
        }

        _rig.Broker.Depth(queue).Should().Be(0);
        _rig.Broker.DeadLetters().Should().ContainSingle();
    }

    [Fact]
    public async Task W08_금지_전이는_영수증_valid_false_상태_불변_invalid_발행_ack()
    {
        await _rig.SendAsync(Batch(1, Status("R-1", 0, 1, "task_assigned")));
        await _rig.SendAsync(Batch(2, Status("R-1", 1, 4, "unload_done")));   // Moving → Unloading(unload_done): 표에 없음
        var worker = _rig.Worker(WorkerRig.Single());
        await RunningAsync(worker.RunAsync, async () =>
        {
            await _rig.DrainedAsync();
            return 0;
        });

        var log = _rig.Store.StatusLog();
        log.Should().HaveCount(2);
        log.Single(s => s.To == 4).Valid.Should().BeFalse();
        (await _rig.Store.GetRobotStatesAsync(Map, ["R-1"], default))["R-1"].Status.Should().Be(1);
        _rig.Broker.InvalidMessages().Should().ContainSingle();
    }

    [Fact]
    public async Task W09_재전달된_상태_변경은_오탐하지_않는다()
    {
        var batch = Batch(1, Status("R-1", 0, 1, "task_assigned"));
        await _rig.SendAsync(batch);
        var worker = _rig.Worker(WorkerRig.Single());
        await RunningAsync(worker.RunAsync, async () =>
        {
            await WorkerRig.Until(() => _rig.Store.StatusLog().Count == 1);
            // 같은 eventId가 다시 온다(부분 발행 재전송 · 재전달)
            var env = EventEnvelope.From(batch, 0);
            await _rig.Broker.PublishBatchAsync([new OutgoingMessage(EventCategory.Task, "R-1", env.EventId, env.EventType, true,
                FleetJson.Serialize(env))], default);
            await _rig.DrainedAsync();
            return 0;
        });

        _rig.Store.StatusLog().Should().ContainSingle().Which.Valid.Should().BeTrue();
        _rig.Broker.InvalidMessages().Should().BeEmpty();
    }
}
