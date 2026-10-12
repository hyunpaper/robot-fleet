using Microsoft.Extensions.Time.Testing;
using RobotFleet.Infrastructure.InMemory;
using static RobotFleet.Pipeline.Tests.TestBatches;

namespace RobotFleet.Pipeline.Tests;

/// R-01~R-07 (10 §7 · §12). R-02 · R-06의 명부 몫은 RosterContract
public sealed class AssignmentTests
{
    private readonly WorkerRig _rig = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-10-12T00:00:00Z"));

    private static async Task TwiceAsync(params RobotFleet.EventWorker.AssignmentCoordinator[] workers)
    {
        for (var i = 0; i < 2; i++)
            foreach (var w in workers)
                await w.RebalanceOnceAsync(default);
    }

    [Fact]
    public async Task R01_몫은_p_mod_N_eq_rank()
    {
        var roster = new InMemoryPartitionRoster(_time);
        var a = _rig.Worker(roster, WorkerRig.Options("w-a"), time: _time);
        var b = _rig.Worker(roster, WorkerRig.Options("w-b"), time: _time);
        await a.RebalanceOnceAsync(default);
        await b.RebalanceOnceAsync(default);
        await TwiceAsync(a, b);
        a.OwnedPartitions.Should().Equal(0, 2, 4, 6, 8, 10, 12, 14);
        b.OwnedPartitions.Should().Equal(1, 3, 5, 7, 9, 11, 13, 15);

        var c = _rig.Worker(roster, WorkerRig.Options("w-c"), time: _time);
        await c.RebalanceOnceAsync(default);
        await TwiceAsync(a, b, c);
        a.OwnedPartitions.Should().Equal(0, 3, 6, 9, 12, 15);
        b.OwnedPartitions.Should().Equal(1, 4, 7, 10, 13);
        c.OwnedPartitions.Should().Equal(2, 5, 8, 11, 14);
        await Task.WhenAll(a.KillAsync(), b.KillAsync(), c.KillAsync());
    }

    [Fact]
    public async Task R03_새_몫_구독이_옛_몫_해제보다_먼저()
    {
        var roster = new InMemoryPartitionRoster(_time);
        var recording = new RecordingSubscriber(_rig.Broker);
        var a = _rig.Worker(roster, WorkerRig.Options("w-a"), recording, _time);
        var b = _rig.Worker(roster, WorkerRig.Options("w-b"), time: _time);
        await a.RebalanceOnceAsync(default);
        await TwiceAsync(a);   // 혼자: 16개 전부
        await b.RebalanceOnceAsync(default);
        var c = _rig.Worker(roster, WorkerRig.Options("w-c"), time: _time);
        await c.RebalanceOnceAsync(default);
        while (recording.Log.TryDequeue(out _)) { }

        await TwiceAsync(a);   // 16개 → p % 3 == 0: 추가 없이 해제만
        recording.Log.Should().OnlyContain(e => e.StartsWith('-'));
        while (recording.Log.TryDequeue(out _)) { }

        await b.KillAsync();
        await roster.LeaveAsync("w-b", default);
        await TwiceAsync(a);   // 3명 → 2명(a, c): a는 0,3,6.. → 0,2,4..: 추가와 해제가 함께
        var log = recording.Log.ToList();
        log.Should().Contain(e => e.StartsWith('+')).And.Contain(e => e.StartsWith('-'));
        log.FindLastIndex(e => e.StartsWith('+')).Should().BeLessThan(log.FindIndex(e => e.StartsWith('-')));
        await Task.WhenAll(a.KillAsync(), c.KillAsync());
    }

    [Fact]
    public async Task R04_재배정을_거쳐도_로봇별_순서_유지_invalid_0()
    {
        var roster = new InMemoryPartitionRoster(TimeProvider.System);
        var a = _rig.Worker(roster, WorkerRig.Options("w-a"));
        using var ctsA = new CancellationTokenSource();
        var runA = a.RunAsync(ctsA.Token);

        // 로봇 40대 × 250단계 = 10,000건: Idle→Moving→Waiting→Moving→Loading→Moving→Unloading→Idle 반복
        (int From, int To, string Reason)[] cycle =
        [
            (0, 1, "task_assigned"), (1, 2, "conflict_wait"), (2, 1, "path_cleared"), (1, 3, "arrived_origin"),
            (3, 1, "load_done"), (1, 4, "arrived_destination"), (4, 0, "unload_done"),
        ];
        var robots = Enumerable.Range(0, 40).Select(i => $"R-{i:D2}").ToArray();
        using var ctsB = new CancellationTokenSource();
        Task? runB = null;
        for (var step = 0; step < 250; step++)
        {
            var (from, to, reason) = cycle[step % cycle.Length];
            await _rig.SendAsync(Batch(step, robots.Select(r => Status(r, from, to, reason)).ToArray()));
            if (step == 60)
                runB = _rig.Worker(roster, WorkerRig.Options("w-b")).RunAsync(ctsB.Token);   // 합류 → 재배정
            if (step == 160)
                ctsB.Cancel();   // 탈퇴 → 재배정
            if (step % 20 == 0) await Task.Delay(30);
        }

        await _rig.DrainedAsync();
        ctsA.Cancel();
        await runA;
        await runB!;

        var log = _rig.Store.StatusLog();
        log.Should().HaveCount(10_000);
        log.Should().OnlyContain(s => s.Valid);
        _rig.Broker.InvalidMessages().Should().BeEmpty();
        foreach (var r in robots)
            (await _rig.Store.GetRobotStatesAsync(Map, [r], default))[r].Status.Should().Be((short)cycle[249 % cycle.Length].To);
    }

    [Fact]
    public async Task R05_두_번_연속_같은_결과일_때만_적용()
    {
        var roster = new InMemoryPartitionRoster(_time);
        var a = _rig.Worker(roster, WorkerRig.Options("w-a"), time: _time);
        await a.RebalanceOnceAsync(default);
        a.OwnedPartitions.Should().BeEmpty();
        await a.RebalanceOnceAsync(default);
        a.OwnedPartitions.Should().HaveCount(16);
        await a.KillAsync();
    }

    [Fact]
    public async Task R06_침묵한_워커가_빠지면_남은_워커가_몫을_넓힌다()
    {
        var roster = new InMemoryPartitionRoster(_time);
        var a = _rig.Worker(roster, WorkerRig.Options("w-a"), time: _time);
        var b = _rig.Worker(roster, WorkerRig.Options("w-b"), time: _time);
        await a.RebalanceOnceAsync(default);
        await b.RebalanceOnceAsync(default);
        await TwiceAsync(a, b);
        a.OwnedPartitions.Should().HaveCount(8);

        await b.KillAsync();   // heartbeat 멈춤
        _time.Advance(TimeSpan.FromSeconds(30));
        await TwiceAsync(a);
        a.OwnedPartitions.Should().HaveCount(16);
        await a.KillAsync();
    }

    [Fact]
    public async Task R07_명부_미설정은_전부_기동부터_불통도_전부_실행_중_불통은_고정()
    {
        var single = _rig.Worker(WorkerRig.Single());
        await single.RebalanceOnceAsync(default);
        single.OwnedPartitions.Should().HaveCount(16);
        await single.KillAsync();

        var down = new InMemoryPartitionRoster(_time);
        down.Disconnect();
        var x = _rig.Worker(down, WorkerRig.Options("w-x"), time: _time);
        await x.RebalanceOnceAsync(default);
        await x.RebalanceOnceAsync(default);
        x.OwnedPartitions.Should().HaveCount(16);
        await x.KillAsync();

        var roster = new InMemoryPartitionRoster(_time);
        var a = _rig.Worker(roster, WorkerRig.Options("w-a"), time: _time);
        var b = _rig.Worker(roster, WorkerRig.Options("w-b"), time: _time);
        await a.RebalanceOnceAsync(default);
        await b.RebalanceOnceAsync(default);
        await TwiceAsync(a, b);
        roster.Disconnect();
        await TwiceAsync(a);
        a.OwnedPartitions.Should().HaveCount(8);   // 끊긴 동안 "나 혼자"로 계산하지 않는다
        await Task.WhenAll(a.KillAsync(), b.KillAsync());
    }
}
