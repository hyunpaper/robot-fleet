using FluentAssertions;
using RobotFleet.FleetController.Arbitration;
using RobotFleet.Shared.Geometry;
using RobotFleet.Shared.Map;
using RobotFleet.Shared.Tests;
using Xunit;

namespace RobotFleet.FleetController.Tests;

/// DL-02(해소)·DL-03·DL-05. 13 §4 · §5
public sealed class DeadlockResolverTests
{
    private static DeadlockResolver New() => new(new DeadlockOptions(), tickMs: 100);

    // 두 로봇 정면 대치 입력(서로의 칸을 원함)
    private static (Dictionary<string, string> Edges, Dictionary<string, int> Wait, Dictionary<string, int> Prio,
        Dictionary<string, Point> Pos, Func<Point, bool> Reserved) HeadOn(
        string a, Point ap, string b, Point bp, int waitMs = 2000, int prioA = 0, int prioB = 0)
    {
        var pos = new Dictionary<string, Point>(StringComparer.Ordinal) { [a] = ap, [b] = bp };
        var occupied = new HashSet<Point>(pos.Values);
        return (
            new(StringComparer.Ordinal) { [a] = b, [b] = a },
            new(StringComparer.Ordinal) { [a] = waitMs, [b] = waitMs },
            new(StringComparer.Ordinal) { [a] = prioA, [b] = prioB },
            pos,
            occupied.Contains);
    }

    [Fact]
    public void 회피_지점_있는_쪽을_양보시킨다()
    {
        // 111111 / 001000 — R-001(2,0)만 포켓 (2,1)에 닿는다. R-002(3,0)는 닿지 못한다
        var map = TestMaps.PocketCorridor();
        var (edges, wait, prio, pos, reserved) = HeadOn("R-001", new(2, 0), "R-002", new(3, 0));

        var infos = New().Resolve(10, edges, wait, prio, pos, map, reserved);

        infos.Should().ContainSingle();
        infos[0].Phase.Should().Be(DeadlockPhase.Detected);
        infos[0].YieldRobotId.Should().Be("R-001");
        infos[0].YieldTo.Should().Be(new Point(2, 1));
        infos[0].SearchedDistance.Should().Be(1);
        infos[0].IsNew.Should().BeTrue();
    }

    [Fact]
    public void 회피_지점이_없으면_failed()
    {
        var map = TestMaps.DeadEndCorridor();   // 1111 — 통로 밖 칸 없음
        var (edges, wait, prio, pos, reserved) = HeadOn("R-001", new(1, 0), "R-002", new(2, 0));

        var infos = New().Resolve(10, edges, wait, prio, pos, map, reserved);

        infos.Should().ContainSingle();
        infos[0].Phase.Should().Be(DeadlockPhase.Failed);
        infos[0].YieldRobotId.Should().BeNull();
        infos[0].YieldTo.Should().BeNull();
        infos[0].SearchedDistance.Should().Be(new DeadlockOptions().YieldSearchMaxDistance + 2);   // 확대 상한까지 봤다
    }

    [Fact]
    public void 비용_최소_로봇을_고른다_가까운_쪽()
    {
        // 1111111 / 0101010 — 포켓 (1,1)·(3,1)·(5,1). R-001(2,0)→(1,1) 두 칸, R-002(3,0)→(3,1) 한 칸
        var map = TestMaps.Unchecked("1111111", "0101010");
        var (edges, wait, prio, pos, reserved) = HeadOn("R-001", new(2, 0), "R-002", new(3, 0));

        var infos = New().Resolve(10, edges, wait, prio, pos, map, reserved);

        infos[0].YieldRobotId.Should().Be("R-002");   // 더 가까운 포켓(비용 2 < 3)
        infos[0].YieldTo.Should().Be(new Point(3, 1));
    }

    [Fact]
    public void 비용_동점이면_robotId_오름차순()
    {
        // 대칭: 둘 다 한 칸 포켓. 0101010에서 (1,1)·(3,1). R-002(2,0)→(1,1), R-001(3,0)→(3,1) 둘 다 한 칸
        var map = TestMaps.Unchecked("1111111", "0101010");
        var (edges, wait, prio, pos, reserved) = HeadOn("R-002", new(2, 0), "R-001", new(3, 0));

        var infos = New().Resolve(10, edges, wait, prio, pos, map, reserved);

        infos[0].YieldRobotId.Should().Be("R-001");   // 비용 같으면 작은 robotId
    }

    [Fact]
    public void 우선순위_항은_1_나누기_우선순위()
    {
        // 거리 동점(둘 다 한 칸). 비용 = 1·거리 + 1·(1/우선순위). R-001 우선순위 1 → 1.0, R-002 우선순위 10 → 0.1
        var map = TestMaps.Unchecked("1111111", "0101010");
        var (edges, wait, prio, pos, reserved) = HeadOn("R-001", new(2, 0), "R-002", new(3, 0), prioA: 1, prioB: 10);

        var infos = New().Resolve(10, edges, wait, prio, pos, map, reserved);

        infos[0].YieldRobotId.Should().Be("R-002");   // 1+0.1=1.1 < 1+1.0=2.0
    }

    [Fact]
    public void 같은_순환_재탐지는_재결정하지_않는다()
    {
        var map = TestMaps.PocketCorridor();
        var (edges, wait, prio, pos, reserved) = HeadOn("R-001", new(2, 0), "R-002", new(3, 0));
        var resolver = New();

        resolver.Resolve(10, edges, wait, prio, pos, map, reserved).Should().ContainSingle();
        resolver.Resolve(11, edges, wait, prio, pos, map, reserved).Should().BeEmpty();   // 락 유지
    }

    [Fact]
    public void 양보_완료를_점유로_감지하고_락을_푼다()
    {
        var map = TestMaps.PocketCorridor();
        var (edges, wait, prio, pos, reserved) = HeadOn("R-001", new(2, 0), "R-002", new(3, 0));
        var resolver = New();
        resolver.Resolve(10, edges, wait, prio, pos, map, reserved);

        // 아직 포켓 미도착
        resolver.DetectCompletions(15, pos).Should().BeEmpty();

        // R-001이 포켓에 도착
        pos["R-001"] = new(2, 1);
        var done = resolver.DetectCompletions(18, pos);

        done.Should().ContainSingle();
        done[0].Phase.Should().Be(DeadlockPhase.Resolved);
        done[0].ResolvedMs.Should().Be((18 - 10) * 100);

        // 락이 풀려 재탐지 가능
        resolver.Resolve(19, edges, wait, prio, pos, map, reserved).Should().ContainSingle();
    }

    [Fact]
    public void TTL_만료되면_failed로_발행하고_경과를_싣는다()
    {
        var map = TestMaps.PocketCorridor();
        var (edges, wait, prio, pos, reserved) = HeadOn("R-001", new(2, 0), "R-002", new(3, 0));
        var opts = new DeadlockOptions { DeadlockResolveTtlTicks = 50 };
        var resolver = new DeadlockResolver(opts, tickMs: 100);
        resolver.Resolve(10, edges, wait, prio, pos, map, reserved);   // TTL 만료 틱 = 60

        resolver.DetectCompletions(59, pos).Should().BeEmpty();
        var failed = resolver.DetectCompletions(60, pos);

        failed.Should().ContainSingle();
        failed[0].Phase.Should().Be(DeadlockPhase.Failed);
        failed[0].YieldRobotId.Should().Be("R-001");   // TTL 만료 failed는 yield_robot_id가 있다(§10)
        failed[0].ResolvedMs.Should().Be((60 - 10) * 100);
    }

    [Fact]
    public void 드레인은_멱등이다()
    {
        var map = TestMaps.PocketCorridor();
        var (edges, wait, prio, pos, reserved) = HeadOn("R-001", new(2, 0), "R-002", new(3, 0));
        var resolver = New();
        resolver.Resolve(10, edges, wait, prio, pos, map, reserved);

        var first = resolver.DrainPending(30);
        var second = resolver.DrainPending(40);

        first.Should().ContainSingle();
        first[0].Phase.Should().Be(DeadlockPhase.Failed);
        second.Should().BeEquivalentTo(first);   // 같은 목록
    }
}
