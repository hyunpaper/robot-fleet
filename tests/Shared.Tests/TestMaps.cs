using RobotFleet.Shared.Map;

namespace RobotFleet.Shared.Tests;

/// <summary>코드로 만드는 작은 맵. FleetController.Tests도 이 파일을 링크해 쓴다.</summary>
public static class TestMaps
{
    /// <summary>전부 Aisle인 w×h 맵.</summary>
    public static GridMap Open(int w, int h) => FromRows(Enumerable.Repeat(new string('1', w), h).ToArray());

    public static GridMap FromRows(params string[] rows) => GridMap.FromDefinition(Definition(rows));

    /// <summary>검증(V8~V14) 없이 생성. 섬이 있는 맵처럼 로더가 거부하는 맵으로 플래너를 시험할 때.</summary>
    public static GridMap Unchecked(params string[] rows) => GridMap.CreateUnchecked(Definition(rows));

    public static MapDefinition Definition(params string[] rows) =>
        new("test", 1, rows[0].Length, rows.Length, 1.0, rows,
            Array.Empty<StationDef>(), Array.Empty<ChargerDef>(), Array.Empty<ParkingDef>());
}
