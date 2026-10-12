using FluentAssertions;
using RobotFleet.Shared.Geometry;
using RobotFleet.Shared.Map;
using Xunit;

namespace RobotFleet.Shared.Tests;

public class MapValidationTests
{
    private static string MapPath(string name) => Path.Combine(AppContext.BaseDirectory, "maps", name);

    private static MapDefinition Def(string[] rows, int? width = null, int? height = null,
        StationDef[]? stations = null, ChargerDef[]? chargers = null, ParkingDef[]? parkings = null) =>
        new("test", 1, width ?? rows[0].Length, height ?? rows.Length, 1.0, rows,
            stations ?? Array.Empty<StationDef>(), chargers ?? Array.Empty<ChargerDef>(), parkings ?? Array.Empty<ParkingDef>());

    private static IReadOnlyList<string> Errors(MapDefinition def)
    {
        var act = () => GridMap.FromDefinition(def);
        return act.Should().Throw<MapValidationException>().Which.Errors;
    }

    [Fact] // S-04
    public void Load_FactoryA()
    {
        var map = MapLoader.LoadFromFile(MapPath("factory-a.json"));

        map.Width.Should().Be(60);
        map.Height.Should().Be(40);
        map.Stations.Should().HaveCount(16);
        map.Chargers.Should().HaveCount(4);
        map.Parkings.Should().HaveCount(10);
        map[6, 3].Should().Be(CellType.Station);
        map[2, 2].Should().Be(CellType.Blocked);
    }

    [Fact] // S-05
    public void Load_TestCorridor()
    {
        var map = MapLoader.LoadFromFile(MapPath("test-corridor.json"));

        map[4, 3].Should().Be(CellType.Aisle);
        map[3, 3].Should().Be(CellType.Blocked);
        map.IsWalkable(new Point(4, 3)).Should().BeTrue();
    }

    [Fact] // S-06 (V5)
    public void RowCountMismatch() =>
        Errors(Def(new[] { "111", "111" }, height: 3)).Should().Contain("grid has 2 rows, expected 3");

    [Fact] // S-07 (V6)
    public void RowLengthMismatch() =>
        Errors(Def(new[] { "11111", "1111" }, width: 5)).Should().Contain("grid row 1 has length 4, expected 5");

    [Fact] // S-08 (V7)
    public void InvalidCellChars() =>
        Errors(Def(new[] { "151", "11x" })).Should().BeEquivalentTo(
            "grid[0][1] = '5' is not a valid cell type (0-4)",
            "grid[1][2] = 'x' is not a valid cell type (0-4)");

    [Fact] // S-09 (V8)
    public void StationOnNonStationCell() =>
        Errors(Def(new[] { "111", "111", "111" }, stations: new[] { new StationDef("ST-01", 2, 2, "pickup", "EQ-01") }))
            .Should().Contain("station ST-01 at (2,2) is not a Station cell");

    [Fact] // S-10 (V9)
    public void StationCellWithoutEntry() =>
        Errors(Def(new[] { "111", "121", "111" })).Should().Equal("Station cell (1,1) has no entry in stations");

    [Fact] // V10 — chargers·parkings 양방향
    public void ChargerAndParkingMismatch() =>
        Errors(Def(new[] { "131", "141" },
                chargers: new[] { new ChargerDef("CH-01", 0, 0) }, parkings: new[] { new ParkingDef("PK-01", 1, 1) }))
            .Should().BeEquivalentTo(
                "charger CH-01 at (0,0) is not a Charge cell",
                "Charge cell (1,0) has no entry in chargers");

    [Fact] // V8 — 범위 밖 좌표
    public void StationOutOfBounds() =>
        Errors(Def(new[] { "111" }, stations: new[] { new StationDef("ST-01", 9, 0, "pickup", "EQ-01") }))
            .Should().Equal("station ST-01 at (9,0) is not a Station cell");

    [Fact] // S-11 (V11)
    public void DuplicateId()
    {
        var st = new StationDef("ST-01", 0, 0, "pickup", "EQ-01");
        Errors(Def(new[] { "212" }, stations: new[] { st, st with { X = 2 } })).Should().Equal("duplicate id ST-01");
    }

    [Fact] // V11 — 종류가 달라도 id 공간은 하나
    public void DuplicateId_AcrossKinds() =>
        Errors(Def(new[] { "34" }, chargers: new[] { new ChargerDef("X-1", 0, 0) }, parkings: new[] { new ParkingDef("X-1", 1, 0) }))
            .Should().Equal("duplicate id X-1");

    [Fact] // V12
    public void UnknownStationType() =>
        Errors(Def(new[] { "2" }, stations: new[] { new StationDef("ST-01", 0, 0, "storage", "EQ-01") }))
            .Should().Equal("station ST-01 has unknown type 'storage'");

    [Fact] // V1~V4
    public void HeaderFields()
    {
        var def = new MapDefinition("", 0, 0, 501, 0, Array.Empty<string>(),
            Array.Empty<StationDef>(), Array.Empty<ChargerDef>(), Array.Empty<ParkingDef>());

        Errors(def).Should().Contain(new[]
        {
            "mapId is required", "version must be >= 1", "width must be in [1, 500]",
            "height must be in [1, 500]", "cellSizeMeters must be > 0",
        });
    }

    [Fact] // V13
    public void NoWalkableCell() =>
        Errors(Def(new[] { "000", "000" })).Should().Equal("map has no walkable cell");

    [Fact] // S-12 (V14)
    public void Island() =>
        Errors(Def(new[] { "11011", "11011", "11011" })).Should().Equal("6 walkable cells are unreachable, e.g. (3,0)");

    [Fact] // S-13 — V5·V7·V11 동시 위반을 한 번에
    public void ErrorsAccumulate()
    {
        var st = new StationDef("ST-01", 0, 0, "pickup", "EQ-01");
        Errors(Def(new[] { "212", "1x1" }, height: 3, stations: new[] { st, st with { X = 2 } }))
            .Should().HaveCount(3)
            .And.Contain("grid has 2 rows, expected 3")
            .And.Contain("grid[1][1] = 'x' is not a valid cell type (0-4)")
            .And.Contain("duplicate id ST-01");
    }

    [Fact] // S-14 — 필수 필드 누락은 JsonException이 아니라 검증 오류
    public void MissingMapId_IsValidationError()
    {
        const string json = """
            { "version": 1, "width": 2, "height": 1, "cellSizeMeters": 1.0, "grid": ["11"],
              "stations": [], "chargers": [], "parkings": [] }
            """;
        var act = () => MapLoader.LoadFromJson(json);
        act.Should().Throw<MapValidationException>().Which.Errors.Should().Equal("mapId is required");
    }

    [Fact] // S-14 보강 — 거의 빈 JSON도 JsonException 없이 검증 오류로
    public void EmptyObject_IsValidationError()
    {
        var act = () => MapLoader.LoadFromJson("{}");
        act.Should().Throw<MapValidationException>().Which.Errors.Should().Contain("mapId is required");
    }
}
