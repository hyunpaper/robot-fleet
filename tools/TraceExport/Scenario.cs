using RobotFleet.Shared.Geometry;

namespace RobotFleet.TraceExport;

/// <summary>들를 장소와 도착 후 머무는 시간. 마지막 정류장은 머무름 없이 그대로 멈춘다.</summary>
public sealed record Stop(string PlaceId, double DwellSeconds = 0);

public sealed record RobotPlan(string RobotId, string StartId, Heading StartHeading, double StartDelaySeconds, IReadOnlyList<Stop> Stops);

public static class Scenarios
{
    // M1에는 충돌 회피가 없어 출발 시각과 목표를 골라 같은 칸 동시 점유가 없게 맞췄다. TraceChecks가 검증한다
    public static readonly IReadOnlyList<RobotPlan> FactoryA = new[]
    {
        new RobotPlan("R-001", "PK-02", Heading.North, 0, new[] { new Stop("ST-01", 3), new Stop("ST-02", 3), new Stop("CH-02") }),
        new RobotPlan("R-002", "PK-01", Heading.North, 2, new[] { new Stop("ST-05", 3), new Stop("ST-06", 3), new Stop("CH-01") }),
        new RobotPlan("R-003", "PK-10", Heading.North, 1, new[] { new Stop("ST-13", 3), new Stop("ST-14", 3), new Stop("CH-03") }),
        new RobotPlan("R-004", "PK-07", Heading.North, 5, new[] { new Stop("ST-11", 3), new Stop("ST-12", 3), new Stop("PK-06") }),
        new RobotPlan("R-005", "PK-06", Heading.North, 0, new[] { new Stop("ST-03", 3), new Stop("ST-04", 3), new Stop("PK-07") }),
        new RobotPlan("R-006", "PK-09", Heading.North, 3, new[] { new Stop("ST-15", 3), new Stop("ST-16", 3), new Stop("CH-04") }),
    };
}
