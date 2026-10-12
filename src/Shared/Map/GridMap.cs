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
        var errors = Validate(def);
        if (errors.Count > 0) throw new MapValidationException(errors);
        return CreateUnchecked(def);
    }

    /// <summary>검증 없이 생성. 테스트에서 섬이 있는 맵 등을 만들 때만 쓴다. 그리드 모양은 호출자가 보장한다.</summary>
    internal static GridMap CreateUnchecked(MapDefinition def)
    {
        var cells = new CellType[def.Width * def.Height];
        for (var y = 0; y < def.Height; y++)
            for (var x = 0; x < def.Width; x++)
                cells[y * def.Width + x] = (CellType)(def.Grid[y][x] - '0');
        return new GridMap(def, cells);
    }

    private const int MaxSize = 500;

    private static List<string> Validate(MapDefinition def)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(def.MapId)) errors.Add("mapId is required");                       // V1
        if (def.Version < 1) errors.Add("version must be >= 1");                                           // V2
        var widthOk = def.Width is >= 1 and <= MaxSize;                                                  // V3
        var heightOk = def.Height is >= 1 and <= MaxSize;
        if (!widthOk) errors.Add($"width must be in [1, {MaxSize}]");
        if (!heightOk) errors.Add($"height must be in [1, {MaxSize}]");
        if (!(def.CellSizeMeters > 0)) errors.Add("cellSizeMeters must be > 0");                           // V4 (NaN 포함)

        var gridOk = widthOk && heightOk;
        if (def.Grid.Count != def.Height)                                                                  // V5
        {
            errors.Add($"grid has {def.Grid.Count} rows, expected {def.Height}");
            gridOk = false;
        }
        for (var y = 0; y < def.Grid.Count; y++)
        {
            var row = def.Grid[y] ?? "";
            if (row.Length != def.Width)                                                                   // V6
            {
                errors.Add($"grid row {y} has length {row.Length}, expected {def.Width}");
                gridOk = false;
            }
            for (var x = 0; x < row.Length; x++)                                                           // V7
            {
                if (row[x] is < '0' or > '4')
                {
                    errors.Add($"grid[{y}][{x}] = '{row[x]}' is not a valid cell type (0-4)");
                    gridOk = false;
                }
            }
        }

        ValidateIds(def, errors);                                                                          // V11
        foreach (var s in def.Stations)                                                                    // V12
            if (s.Type is not ("pickup" or "dropoff"))
                errors.Add($"station {s.Id} has unknown type '{s.Type}'");

        // 그리드 모양이 깨졌으면 좌표 기반 검사(V8~V10, V13, V14)는 연쇄 오류만 늘리므로 건너뛴다
        if (!gridOk) return errors;

        var map = CreateUnchecked(def);
        ValidatePlaces(map, def.Stations.Select(s => (s.Id, s.X, s.Y)), CellType.Station, "station", "stations", errors);
        ValidatePlaces(map, def.Chargers.Select(c => (c.Id, c.X, c.Y)), CellType.Charge, "charger", "chargers", errors);
        ValidatePlaces(map, def.Parkings.Select(p => (p.Id, p.X, p.Y)), CellType.Parking, "parking", "parkings", errors);
        ValidateConnectivity(map, errors);
        return errors;
    }

    private static void ValidateIds(MapDefinition def, List<string> errors)
    {
        var seen = new HashSet<string>();
        var reported = new HashSet<string>();
        var ids = def.Stations.Select(s => s.Id).Concat(def.Chargers.Select(c => c.Id)).Concat(def.Parkings.Select(p => p.Id));
        foreach (var id in ids)
            if (!seen.Add(id) && reported.Add(id))
                errors.Add($"duplicate id {id}");
    }

    // V8~V10: 목록 → 칸, 칸 → 목록 양방향 대조
    private static void ValidatePlaces(
        GridMap map, IEnumerable<(string Id, int X, int Y)> places, CellType type, string label, string listName,
        List<string> errors)
    {
        var listed = new HashSet<Point>();
        foreach (var (id, x, y) in places)
        {
            var p = new Point(x, y);
            if (!map.InBounds(p) || map[p] != type)
                errors.Add($"{label} {id} at ({x},{y}) is not a {type} cell");
            listed.Add(p);
        }

        for (var y = 0; y < map.Height; y++)
            for (var x = 0; x < map.Width; x++)
                if (map[x, y] == type && !listed.Contains(new Point(x, y)))
                    errors.Add($"{type} cell ({x},{y}) has no entry in {listName}");
    }

    // V13, V14: 행 우선 첫 통행 칸에서 BFS
    private static void ValidateConnectivity(GridMap map, List<string> errors)
    {
        var start = -1;
        var walkable = 0;
        for (var i = 0; i < map._cells.Length; i++)
        {
            if (map._cells[i] == CellType.Blocked) continue;
            if (start < 0) start = i;
            walkable++;
        }
        if (start < 0)
        {
            errors.Add("map has no walkable cell");
            return;
        }

        var visited = new bool[map._cells.Length];
        var queue = new Queue<Point>();
        visited[start] = true;
        queue.Enqueue(new Point(start % map.Width, start / map.Width));
        var reached = 1;
        while (queue.Count > 0)
        {
            foreach (var n in map.WalkableNeighbors(queue.Dequeue()))
            {
                var i = n.Y * map.Width + n.X;
                if (visited[i]) continue;
                visited[i] = true;
                reached++;
                queue.Enqueue(n);
            }
        }

        if (reached == walkable) return;
        for (var i = 0; i < map._cells.Length; i++)
        {
            if (map._cells[i] == CellType.Blocked || visited[i]) continue;
            errors.Add($"{walkable - reached} walkable cells are unreachable, e.g. ({i % map.Width},{i / map.Width})");
            return;
        }
    }
}
