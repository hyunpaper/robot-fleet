using FluentAssertions;
using RobotFleet.FleetController.Arbitration;
using RobotFleet.Shared.Geometry;
using Xunit;

namespace RobotFleet.FleetController.Tests;

public class InMemoryMoveArbiterTests
{
    private const string Map = "m";

    private static InMemoryMoveArbiter Arbiter(params (string Id, Point Cell)[] robots)
    {
        var arbiter = new InMemoryMoveArbiter();
        foreach (var (id, cell) in robots) arbiter.Register(Map, id, cell);
        return arbiter;
    }

    private static MoveRequest Req(string id, Point from, Point to, int waitMs = 0, int remaining = 10, int taskPriority = 0)
        => new(id, from, to, HeadingExtensions.HeadingTo(from, to), 1, waitMs, remaining, taskPriority);

    [Fact]
    public async Task SameCell_HigherPriorityGranted_OtherOccupiedWithBlockedBy()
    {
        var arbiter = Arbiter(("R-001", new(1, 2)), ("R-002", new(2, 1)));
        var requests = new[]
        {
            Req("R-001", new(1, 2), new(2, 2), remaining: 20),
            Req("R-002", new(2, 1), new(2, 2), remaining: 5),     // 목적지에 가까워 점수가 높다
        };

        var decisions = await arbiter.DecideAsync(Map, 1, requests);

        decisions.Select(d => d.RobotId).Should().Equal("R-001", "R-002");
        decisions[1].Granted.Should().BeTrue();
        decisions[0].Should().Be(new MoveDecision("R-001", false, MoveDenyReason.Occupied, BlockedBy: "R-002"));
        arbiter.OccupantOf(Map, new(2, 2)).Should().Be("R-002");
    }

    [Fact]
    public async Task SameCell_TiedScore_LowerRobotIdGranted_RegardlessOfRequestOrder()
    {
        var arbiter = Arbiter(("R-001", new(1, 2)), ("R-002", new(2, 1)));
        var requests = new[]
        {
            Req("R-002", new(2, 1), new(2, 2)),
            Req("R-001", new(1, 2), new(2, 2)),
        };

        var decisions = await arbiter.DecideAsync(Map, 1, requests);

        decisions[0].Should().Be(new MoveDecision("R-002", false, MoveDenyReason.Occupied, BlockedBy: "R-001"));
        decisions[1].Should().Be(MoveDecision.Grant("R-001"));
    }

    [Fact]
    public async Task Swap_LowerPriorityDeniedAsSwap()
    {
        var arbiter = Arbiter(("R-001", new(1, 0)), ("R-002", new(2, 0)));
        var requests = new[]
        {
            Req("R-001", new(1, 0), new(2, 0)),
            Req("R-002", new(2, 0), new(1, 0)),
        };

        var decisions = await arbiter.DecideAsync(Map, 1, requests);

        // 2칸 선점이라 선순위도 상대의 현재 칸에 막힌다. 맞바꿈은 대기로 풀리지 않는다(04 교차 충돌)
        decisions[0].Should().Be(new MoveDecision("R-001", false, MoveDenyReason.Occupied, BlockedBy: "R-002"));
        decisions[1].Should().Be(new MoveDecision("R-002", false, MoveDenyReason.Swap, BlockedBy: "R-001"));
        arbiter.OccupantOf(Map, new(1, 0)).Should().Be("R-001");
        arbiter.OccupantOf(Map, new(2, 0)).Should().Be("R-002");
    }

    [Fact]
    public async Task OccupiedCell_Denied_ThenGrantedAfterRelease()
    {
        var arbiter = Arbiter(("R-001", new(1, 0)), ("R-002", new(3, 0)));

        // R-002가 (2,0)으로 이동 승인 → (3,0)·(2,0) 점유
        (await arbiter.DecideAsync(Map, 1, new[] { Req("R-002", new(3, 0), new(2, 0)) }))[0].Granted.Should().BeTrue();
        // R-002가 (2,0)에서 (1,0)이 아닌 (2,1)로 빠질 때까지 (2,0)은 막힌다
        (await arbiter.DecideAsync(Map, 2, new[] { Req("R-001", new(1, 0), new(2, 0)) }))[0]
            .Should().Be(new MoveDecision("R-001", false, MoveDenyReason.Occupied, BlockedBy: "R-002"));

        await arbiter.ReleaseAsync(Map, "R-002", new(3, 0));
        (await arbiter.DecideAsync(Map, 11, new[] { Req("R-002", new(2, 0), new(2, 1)) }))[0].Granted.Should().BeTrue();
        await arbiter.ReleaseAsync(Map, "R-002", new(2, 0));

        (await arbiter.DecideAsync(Map, 21, new[] { Req("R-001", new(1, 0), new(2, 0)) }))[0].Granted.Should().BeTrue();
        arbiter.OccupantOf(Map, new(3, 0)).Should().BeNull();
        arbiter.OccupantOf(Map, new(2, 1)).Should().Be("R-002");
    }

    [Fact]
    public async Task Following_CannotEnterLeadersFromCell_UntilLeaderCompletes()
    {
        var arbiter = Arbiter(("R-001", new(1, 0)), ("R-002", new(0, 0)));

        var first = await arbiter.DecideAsync(Map, 1, new[]
        {
            Req("R-001", new(1, 0), new(2, 0)),
            Req("R-002", new(0, 0), new(1, 0)),
        });
        first[0].Granted.Should().BeTrue();
        first[1].Should().Be(new MoveDecision("R-002", false, MoveDenyReason.Occupied, BlockedBy: "R-001"));

        // 앞 로봇 이동 중: 출발 칸과 도착 칸 모두 점유
        arbiter.OccupantOf(Map, new(1, 0)).Should().Be("R-001");
        arbiter.OccupantOf(Map, new(2, 0)).Should().Be("R-001");
        (await arbiter.DecideAsync(Map, 5, new[] { Req("R-002", new(0, 0), new(1, 0), waitMs: 400) }))[0]
            .Granted.Should().BeFalse();

        await arbiter.ReleaseAsync(Map, "R-001", new(1, 0));   // 이동 완료
        arbiter.OccupantOf(Map, new(1, 0)).Should().BeNull();
        (await arbiter.DecideAsync(Map, 11, new[] { Req("R-002", new(0, 0), new(1, 0), waitMs: 1000) }))[0]
            .Granted.Should().BeTrue();
    }

    [Fact]
    public async Task IdleRobot_BlocksItsCell()
    {
        var arbiter = Arbiter(("R-001", new(0, 0)), ("R-009", new(1, 0)));

        var decision = (await arbiter.DecideAsync(Map, 1, new[] { Req("R-001", new(0, 0), new(1, 0)) }))[0];

        decision.Should().Be(new MoveDecision("R-001", false, MoveDenyReason.Occupied, BlockedBy: "R-009"));
    }

    [Fact]
    public async Task UnregisteredRobot_IsRegisteredAtFrom()
    {
        var arbiter = new InMemoryMoveArbiter();

        (await arbiter.DecideAsync(Map, 1, new[] { Req("R-001", new(0, 0), new(1, 0)) }))[0].Granted.Should().BeTrue();

        arbiter.OccupantOf(Map, new(0, 0)).Should().Be("R-001");
        arbiter.OccupantOf(Map, new(1, 0)).Should().Be("R-001");
    }

    [Fact]
    public async Task ResentGrantedRequest_IsGrantedAgain()
    {
        var arbiter = Arbiter(("R-001", new(0, 0)));
        var request = Req("R-001", new(0, 0), new(1, 0));

        (await arbiter.DecideAsync(Map, 1, new[] { request }))[0].Granted.Should().BeTrue();
        (await arbiter.DecideAsync(Map, 1, new[] { request }))[0].Granted.Should().BeTrue();
    }

    [Fact]
    public void Register_OnOccupiedCell_Throws()
    {
        var arbiter = Arbiter(("R-001", new(0, 0)));
        arbiter.Invoking(a => a.Register(Map, "R-002", new(0, 0))).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task Maps_AreIndependent()
    {
        var arbiter = new InMemoryMoveArbiter();
        arbiter.Register("a", "R-001", new(0, 0));
        arbiter.Register("b", "R-002", new(1, 0));

        (await arbiter.DecideAsync("a", 1, new[] { Req("R-001", new(0, 0), new(1, 0)) }))[0].Granted.Should().BeTrue();
    }

    [Fact]
    public async Task EmptyBatch_ReturnsEmpty()
    {
        (await new InMemoryMoveArbiter().DecideAsync(Map, 1, Array.Empty<MoveRequest>())).Should().BeEmpty();
    }

    [Fact]
    public void Score_FollowsPolicy()
    {
        MovePriority.Score(new MoveRequest("R", default, default, Heading.East, 1, WaitMs: 1000, RemainingSteps: 30, TaskPriority: 1))
            .Should().Be(10 + 70 + 50);
    }

    [Fact]
    public async Task Starvation_LongWaitEventuallyBeatsCloserRobot()
    {
        // 먼 로봇(잔여 60)과 매번 새로 오는 가까운 로봇(잔여 10)이 같은 칸을 다툰다
        var waitMs = 0;
        for (var tick = 1; ; tick++)
        {
            var arbiter = Arbiter(("R-001", new(0, 1)), ("R-002", new(1, 0)));
            var decisions = await arbiter.DecideAsync(Map, tick, new[]
            {
                Req("R-001", new(0, 1), new(1, 1), waitMs: waitMs, remaining: 60),
                Req("R-002", new(1, 0), new(1, 1), remaining: 10),
            });
            if (decisions[0].Granted)
            {
                // 점수 차 50을 대기 가중(waitMs × 0.01)이 따라잡는 지점
                waitMs.Should().BeInRange(5000, 5100);
                decisions[1].Should().Be(new MoveDecision("R-002", false, MoveDenyReason.Occupied, BlockedBy: "R-001"));
                return;
            }
            decisions[0].BlockedBy.Should().Be("R-002");
            waitMs += 100;
            tick.Should().BeLessThan(1000);
        }
    }
}
