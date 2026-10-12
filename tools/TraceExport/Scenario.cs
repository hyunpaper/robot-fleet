using RobotFleet.Shared.Geometry;

namespace RobotFleet.TraceExport;

/// <summary>들를 장소와 도착 후 최소 머무는 시간.</summary>
public sealed record Stop(string PlaceId, double MinDwellSeconds);

/// <summary>Loop[0](주차 칸)에 서서 StartDelaySeconds 뒤 Loop[1]부터 순환한다. 주차 칸에서는 다음 출발 시각(시작 + 주기의 배수)까지 쉰다.</summary>
public sealed record RobotPlan(string RobotId, Heading StartHeading, double StartDelaySeconds, IReadOnlyList<Stop> Loop);

/// <summary>모든 로봇이 CycleSeconds마다 같은 시각에 출발해 전체 상태가 그 주기로 되풀이된다.</summary>
public sealed record ScenarioDef(double CycleSeconds, IReadOnlyList<RobotPlan> Robots);

public static class Scenarios
{
    private const double Home = 3;
    private const double Work = 4;

    // 좌하단 주차 6대 + 우상단 주차 6대. 출발 위상은 교차로 몇 곳에서만 마주쳐 한 대가 대기하고, 1칸 통로 정면 마주침(교착)은 없게 골랐다
    public static readonly ScenarioDef FactoryA = new(140, new[]
    {
        Plan("R-001", "PK-01", Heading.North, 131, "ST-11", "ST-12", "ST-11", "ST-12"),
        Plan("R-002", "PK-02", Heading.North, 65, "ST-09", "ST-10", "ST-09", "ST-10"),
        Plan("R-003", "PK-03", Heading.North, 85, "ST-03", "ST-04"),
        Plan("R-004", "PK-04", Heading.North, 19, "ST-01", "ST-02"),
        Plan("R-005", "PK-05", Heading.North, 34, "ST-15", "ST-16"),
        Plan("R-006", "PK-06", Heading.North, 32, "ST-13", "ST-14"),
        Plan("R-007", "PK-16", Heading.South, 49, "ST-05", "ST-06", "ST-05", "ST-06"),
        Plan("R-008", "PK-15", Heading.South, 63, "ST-07", "ST-08", "ST-07", "ST-08"),
        Plan("R-009", "PK-14", Heading.South, 9, "ST-13", "ST-16"),
        Plan("R-010", "PK-13", Heading.South, 117, "ST-15", "ST-14"),
        Plan("R-011", "PK-12", Heading.South, 59, "ST-01", "ST-04"),
        Plan("R-012", "PK-11", Heading.South, 37, "ST-03", "ST-02"),
    });

    private static RobotPlan Plan(string id, string home, Heading heading, double delay, params string[] stations) =>
        new(id, heading, delay, new[] { new Stop(home, Home) }.Concat(stations.Select(s => new Stop(s, Work))).ToList());
}
