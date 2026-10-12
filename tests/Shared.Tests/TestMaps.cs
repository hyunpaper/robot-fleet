using RobotFleet.Shared.Map;

namespace RobotFleet.Shared.Tests;

public static class TestMaps
{
    /// <summary>전부 Aisle인 w×h 맵.</summary>
    public static GridMap Open(int w, int h) => FromRows(Enumerable.Repeat(new string('1', w), h).ToArray());

    public static GridMap FromRows(params string[] rows) =>
        GridMap.FromDefinition(new MapDefinition(
            "test", 1, rows[0].Length, rows.Length, 1.0, rows,
            Array.Empty<StationDef>(), Array.Empty<ChargerDef>(), Array.Empty<ParkingDef>()));
}
