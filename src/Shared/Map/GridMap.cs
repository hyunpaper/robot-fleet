using RobotFleet.Shared.Geometry;

namespace RobotFleet.Shared.Map;

/// <summary>검증을 통과한 불변 맵. 좌표계는 03 문서(원점 좌상단, Y 아래 증가).</summary>
public sealed class GridMap
{
    private static readonly Heading[] Directions = { Heading.North, Heading.East, Heading.South, Heading.West };

    private readonly CellType[] _cells;   // [y * Width + x]

    public string MapId { get; }
    public int Version { get; }
    public int Width { get; }
    public int Height { get; }
    public double CellSizeMeters { get; }
    public IReadOnlyList<StationDef> Stations { get; }
    public IReadOnlyList<ChargerDef> Chargers { get; }
    public IReadOnlyList<ParkingDef> Parkings { get; }

    private GridMap(MapDefinition def, CellType[] cells)
    {
        MapId = def.MapId;
        Version = def.Version;
        Width = def.Width;
        Height = def.Height;
        CellSizeMeters = def.CellSizeMeters;
        Stations = def.Stations;
        Chargers = def.Chargers;
        Parkings = def.Parkings;
        _cells = cells;
    }

    public CellType this[int x, int y]
    {
        get
        {
            if (x < 0 || x >= Width || y < 0 || y >= Height)
                throw new IndexOutOfRangeException($"({x},{y}) is out of bounds {Width}x{Height}");
            return _cells[y * Width + x];
        }
    }

    public CellType this[Point p] => this[p.X, p.Y];

    public bool InBounds(Point p) => p.X >= 0 && p.X < Width && p.Y >= 0 && p.Y < Height;

    /// <summary>범위 안이고 Blocked가 아님.</summary>
    public bool IsWalkable(Point p) => InBounds(p) && _cells[p.Y * Width + p.X] != CellType.Blocked;

    /// <summary>N, E, S, W 순서로 통행 가능한 인접 칸만. 순서가 고정이라 A* 결과가 결정적이다.</summary>
    public IEnumerable<Point> WalkableNeighbors(Point p)
    {
        foreach (var d in Directions)
        {
            var n = p.Step(d);
            if (IsWalkable(n)) yield return n;
        }
    }

    public StationDef? FindStation(string id) => Stations.FirstOrDefault(s => s.Id == id);

    public ParkingDef? FindParking(string id) => Parkings.FirstOrDefault(p => p.Id == id);

    /// <summary>검증 후 생성. 실패 시 MapValidationException (모든 오류를 모아서 1회 던짐).</summary>
    public static GridMap FromDefinition(MapDefinition def)
    {
        // TODO(core): V1~V14 검증 (오류 누적 후 MapValidationException 1회)
        var cells = new CellType[def.Width * def.Height];
        for (var y = 0; y < def.Height; y++)
            for (var x = 0; x < def.Width; x++)
                cells[y * def.Width + x] = (CellType)(def.Grid[y][x] - '0');
        return new GridMap(def, cells);
    }
}
