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

    /// <summary>회피 포켓이 있는 1칸 통로(교착 DL-07). 정면 대치가 (2,1) 포켓으로의 양보로 풀린다.</summary>
    public static GridMap PocketCorridor() => FromRows(
        "111111",
        "001000");

    /// <summary>회피 지점이 없는 1칸 막다른 통로(교착 DL-08). 정면 대치는 해소 불가.</summary>
    public static GridMap DeadEndCorridor() => FromRows("1111");

    /// <summary>포켓 2개((2,1)·(5,1))가 있는 긴 1칸 통로(교착 DL-09, 3대 행렬 대 1대).</summary>
    public static GridMap TwoPocketCorridor() => FromRows(
        "11111111",
        "00100100");

    public static MapDefinition Definition(params string[] rows) =>
        new("test", 1, rows[0].Length, rows.Length, 1.0, rows,
            Array.Empty<StationDef>(), Array.Empty<ChargerDef>(), Array.Empty<ParkingDef>());
}
