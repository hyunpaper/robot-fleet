using FluentAssertions;
using RobotFleet.FleetController.Arbitration;
using Xunit;

namespace RobotFleet.FleetController.Tests;

/// DL-01 · DL-02(그래프). 13 §3
public sealed class DeadlockGraphTests
{
    private static IReadOnlyList<IReadOnlyList<string>> Find(
        Dictionary<string, string> edges, int threshold = 1000, int waitMs = 1000)
    {
        var wait = edges.Keys.ToDictionary(k => k, _ => waitMs, StringComparer.Ordinal);
        return DeadlockGraph.FindCycles(edges, wait, threshold);
    }

    [Fact]
    public void 정면_대치_2순환()
    {
        var cycles = Find(new() { ["R-002"] = "R-001", ["R-001"] = "R-002" });

        cycles.Should().ContainSingle();
        cycles[0].Should().Equal("R-001", "R-002");   // 최소 robotId에서 시작
    }

    [Fact]
    public void 세_로봇_순환()
    {
        var cycles = Find(new() { ["R-001"] = "R-002", ["R-002"] = "R-003", ["R-003"] = "R-001" });

        cycles.Should().ContainSingle();
        cycles[0].Should().Equal("R-001", "R-002", "R-003");
    }

    [Fact]
    public void 추종은_순환이_아니다()
    {
        // R-002가 R-001을 기다리지만 R-001은 아무도 안 기다린다(승인됨) → 체인이 끝난다
        var cycles = Find(new() { ["R-002"] = "R-001" });

        cycles.Should().BeEmpty();
    }

    [Fact]
    public void 꼬리가_달린_순환은_고리만()
    {
        // X → A → B → A. X는 고리에 끼지 않는다
        var cycles = Find(new() { ["R-009"] = "R-001", ["R-001"] = "R-002", ["R-002"] = "R-001" });

        cycles.Should().ContainSingle();
        cycles[0].Should().Equal("R-001", "R-002");
    }

    [Fact]
    public void 서로소_순환_둘()
    {
        var cycles = Find(new()
        {
            ["R-001"] = "R-002",
            ["R-002"] = "R-001",
            ["R-003"] = "R-004",
            ["R-004"] = "R-003",
        });

        cycles.Should().HaveCount(2);
        cycles[0].Should().Equal("R-001", "R-002");
        cycles[1].Should().Equal("R-003", "R-004");
    }

    [Fact]
    public void 임계_미만_대기는_제외()
    {
        var edges = new Dictionary<string, string>(StringComparer.Ordinal) { ["R-002"] = "R-001", ["R-001"] = "R-002" };
        var wait = new Dictionary<string, int>(StringComparer.Ordinal) { ["R-001"] = 500, ["R-002"] = 900 };

        DeadlockGraph.FindCycles(edges, wait, 1000).Should().BeEmpty();

        wait["R-001"] = 1000;   // 한 대만 임계 넘어도 순환
        DeadlockGraph.FindCycles(edges, wait, 1000).Should().ContainSingle();
    }

    [Fact]
    public void 삽입_순서가_달라도_같은_결과()
    {
        var a = Find(new() { ["R-004"] = "R-003", ["R-001"] = "R-002", ["R-002"] = "R-001", ["R-003"] = "R-004" });
        var b = Find(new() { ["R-001"] = "R-002", ["R-003"] = "R-004", ["R-004"] = "R-003", ["R-002"] = "R-001" });

        a.Should().BeEquivalentTo(b, o => o.WithStrictOrdering());
    }
}
