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
    public void 회피_지점_없으면_발행없이_재시도하고_드레인에서_failed()
    {
        var map = TestMaps.DeadEndCorridor();   // 1111 — 통로 밖 칸 없음
        var (edges, wait, prio, pos, reserved) = HeadOn("R-001", new(1, 0), "R-002", new(2, 0));
        var resolver = New();

        // 회피 지점이 없으면 발행하지 않고 에피소드를 유지(재시도), noYieldRetries 증가
        resolver.Resolve(10, edges, wait, prio, pos, map, reserved).Should().BeEmpty();
        resolver.UnresolvedCount.Should().Be(1);
        resolver.Resolve(20, edges, wait, prio, pos, map, reserved).Should().BeEmpty();   // 재시도 간격(10틱) 경과 → 또 시도

        // run 종료 드레인에서만 failed 1건(noYieldRetries > 0, yield_robot_id NULL)
        var drained = resolver.DrainPending(25);
        drained.Should().ContainSingle();
        drained[0].Phase.Should().Be(DeadlockPhase.Failed);
        drained[0].YieldRobotId.Should().BeNull();
        drained[0].NoYieldRetries.Should().BeGreaterThan(1);
        drained[0].ResolvedMs.Should().Be((25 - 10) * 100);   // 최초 탐지부터 총 경과
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
        done[0].Resolution.Should().Be("yield");
        done[0].ResolvedMs.Should().Be((18 - 10) * 100);   // 최초 탐지부터 총 경과
        resolver.IsYielding("R-001").Should().BeFalse();    // 에피소드 종료
    }

    [Fact]
    public void 양보_TTL_만료는_발행없이_재시도로_전환한다()
    {
        var map = TestMaps.PocketCorridor();
        var (edges, wait, prio, pos, reserved) = HeadOn("R-001", new(2, 0), "R-002", new(3, 0));
        var opts = new DeadlockOptions { DeadlockResolveTtlTicks = 50 };
        var resolver = new DeadlockResolver(opts, tickMs: 100);
        resolver.Resolve(10, edges, wait, prio, pos, map, reserved).Should().ContainSingle();   // 양보 시작(TTL 만료 틱=60)

        resolver.DetectCompletions(60, pos).Should().BeEmpty();   // 미도착 TTL 만료 → 발행 없음
        resolver.IsYielding("R-001").Should().BeFalse();          // 양보 해제(재시도로 전환)
    }

    [Fact]
    public void 포켓이_비면_같은_에피소드가_yield로_해소되고_이벤트는_최초탐지부터_총경과()
    {
        var map = TestMaps.PocketCorridor();   // 포켓 (2,1)
        var pos = new Dictionary<string, Point>(StringComparer.Ordinal) { ["R-001"] = new(2, 0), ["R-002"] = new(3, 0) };
        var edges = new Dictionary<string, string>(StringComparer.Ordinal) { ["R-001"] = "R-002", ["R-002"] = "R-001" };
        var wait = new Dictionary<string, int>(StringComparer.Ordinal) { ["R-001"] = 2000, ["R-002"] = 2000 };
        var prio = new Dictionary<string, int>(StringComparer.Ordinal) { ["R-001"] = 0, ["R-002"] = 0 };
        var pocketBusy = true;
        bool Reserved(Point c) => c == new Point(2, 0) || c == new Point(3, 0) || (pocketBusy && c == new Point(2, 1));
        var resolver = New();

        resolver.Resolve(10, edges, wait, prio, pos, map, Reserved).Should().BeEmpty();   // 포켓 점유 → 양보 못 함(재시도)
        resolver.UnresolvedCount.Should().Be(1);

        pocketBusy = false;   // 포켓이 빔
        resolver.Resolve(20, edges, wait, prio, pos, map, Reserved).Should().ContainSingle();   // 재시도에서 양보 시작

        pos["R-001"] = new(2, 1);   // 포켓 도착
        var done = resolver.DetectCompletions(25, pos);
        done.Should().ContainSingle();
        done[0].Phase.Should().Be(DeadlockPhase.Resolved);
        done[0].Resolution.Should().Be("yield");
        done[0].ResolvedMs.Should().Be((25 - 10) * 100);   // 최초 탐지(10)부터 총 경과(1건만)
    }

    [Fact]
    public void 순환이_소멸하면_유예_뒤_reroute로_종료된다()
    {
        var map = TestMaps.DeadEndCorridor();
        var (edges, wait, prio, pos, reserved) = HeadOn("R-001", new(1, 0), "R-002", new(2, 0));
        var resolver = New();
        resolver.Resolve(10, edges, wait, prio, pos, map, reserved);   // 회피 지점 없음 → 재시도 상태

        var empty = new Dictionary<string, string>(StringComparer.Ordinal);
        var emptyWait = new Dictionary<string, int>(StringComparer.Ordinal);
        resolver.Resolve(12, empty, emptyWait, prio, pos, map, reserved);   // 순환 소멸(구성원이 빠짐) → 부재 시작
        resolver.DetectCompletions(12, pos).Should().BeEmpty();            // 유예 중

        resolver.Resolve(25, empty, emptyWait, prio, pos, map, reserved);
        var rerouted = resolver.DetectCompletions(25, pos);               // 부재 유예(10틱) 경과 → reroute
        rerouted.Should().ContainSingle();
        rerouted[0].Phase.Should().Be(DeadlockPhase.Reroute);
        rerouted[0].Resolution.Should().Be("reroute");
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
