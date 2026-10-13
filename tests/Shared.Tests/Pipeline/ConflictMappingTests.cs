using FluentAssertions;
using Xunit;
using RobotFleet.Shared.Messaging;
using RobotFleet.Shared.Pipeline;

namespace RobotFleet.Shared.Tests.Pipeline;

public sealed class ConflictMappingTests
{
    private static ConflictRow Row(DeadlockDetectedPayload payload) =>
        new("b/0", "run-1", "factory-a", 42, DateTimeOffset.UnixEpoch, EventType.DeadlockDetected,
            FleetJson.ToElement(payload));

    [Fact]
    public void 양보_있는_교착은_yield_좌표를_싣는다()
    {
        var row = Row(new DeadlockDetectedPayload(
            Cycle: new[] { "R-001", "R-002" }, Cells: new[] { new CellDto(3, 4) },
            Resolution: "yield", YieldRobotId: "R-002", YieldToX: 5, YieldToY: 6, SearchedDistance: 4, ResolvedMs: 120));

        var change = StoreMapping.ToConflict(row);

        change.YieldRobotId.Should().Be("R-002");
        change.YieldToX.Should().Be((short)5);
        change.YieldToY.Should().Be((short)6);
        change.SearchedDistance.Should().Be((short)4);
    }

    [Fact]
    public void 양보_없는_교착은_yield_좌표가_NULL이다()
    {
        // 해소 실패 — YieldRobotId 빈 문자열, YieldToX/Y는 기본 0으로 오지만 좌표가 아니다
        var row = Row(new DeadlockDetectedPayload(
            Cycle: new[] { "R-001", "R-002" }, Cells: new[] { new CellDto(3, 4) },
            Resolution: "failed", YieldRobotId: "", YieldToX: 0, YieldToY: 0, SearchedDistance: 5, ResolvedMs: 0));

        var change = StoreMapping.ToConflict(row);

        change.YieldRobotId.Should().BeNull();
        change.YieldToX.Should().BeNull();
        change.YieldToY.Should().BeNull();
        change.Resolution.Should().Be((short)3);   // failed
        change.SearchedDistance.Should().Be((short)5);   // 회피 지점 없어도 탐색 상한은 남는다
    }
}
