# 06. M1 상세 설계 — 맵 + 로봇 1대 + A*

## 범위

| 포함 | 제외 (M2 이후) |
|---|---|
| 맵 JSON 로딩·검증 | 다중 로봇, 충돌·교착 |
| 로봇 1대 | 작업 생성기, 배터리 |
| A* (4방향, 회전 비용, 노드 = `(x, y, heading)`) | Redis, RabbitMQ, HTTP, DB |
| 틱 루프 — 이동 1000ms/칸, 회전 300ms/90도 | 이벤트 발행 |
| 콘솔 출력 | Prometheus |

**완료 기준** — `dotnet run --project src/Simulator -- --map maps/factory-a.json --start 0,39 --goal 6,3`
을 실행하면 로봇이 PK-01에서 ST-01까지 움직이고, 콘솔에 도착 로그가 찍히고, 종료 코드 0으로 끝난다.

단일 프로세스, 외부 인프라 없음. 단 **M2~M4에서 갈아엎지 않도록** 경계를 미리 긋는다.
이 문서의 "M2+ 확장 지점" 항목이 그 경계다.

---

## 솔루션 구조

```
robot-fleet/
├── RobotFleet.sln
├── Directory.Build.props              # net8.0, Nullable, ImplicitUsings, TreatWarningsAsErrors
├── src/
│   ├── Shared/                        # RobotFleet.Shared.csproj (classlib)
│   │   ├── Geometry/   Point.cs, Heading.cs
│   │   ├── Map/        CellType.cs, GridMap.cs, MapDefinition.cs, MapLoader.cs, MapValidationException.cs
│   │   └── Domain/     Robot.cs, RobotStatus.cs, TransportTask.cs, TaskType.cs, TaskStatus.cs
│   ├── FleetController/               # RobotFleet.FleetController.csproj (classlib)
│   │   ├── Planning/   IPathPlanner.cs, AStarPathPlanner.cs, PathResult.cs, PathStep.cs, PathCost.cs
│   │   └── Arbitration/ IMoveArbiter.cs, MoveRequest.cs, MoveDecision.cs, AlwaysGrantMoveArbiter.cs
│   └── Simulator/                     # RobotFleet.Simulator.csproj (console exe)
│       ├── Program.cs                 # 인자 파싱, 조립, 실행
│       ├── SimulationOptions.cs
│       ├── SimulationLoop.cs          # 틱 루프
│       ├── RobotStepper.cs            # 로봇 1대의 틱 진행 (이동·회전)
│       ├── RobotAction.cs
│       ├── ITickDelay.cs              # 실시간 대기 추상화 (테스트에서 제거)
│       ├── ConsoleRenderer.cs
│       └── SimulationStats.cs
├── tests/
│   ├── Shared.Tests/                  # RobotFleet.Shared.Tests.csproj
│   ├── FleetController.Tests/         # RobotFleet.FleetController.Tests.csproj
│   └── Simulator.Tests/               # RobotFleet.Simulator.Tests.csproj
└── maps/
    ├── factory-a.json
    └── test-corridor.json
```

### 프로젝트 참조

```
Simulator ──► FleetController ──► Shared
tests/X.Tests ──► src/X   (+ Shared)
```

| 결정 | 근거 |
|---|---|
| FleetController를 **라이브러리**로 시작 | M1은 Simulator가 in-process로 호출한다. M3에서 `src/FleetController.Api`(ASP.NET)를 추가해 이 라이브러리를 참조하면 된다. 알고리즘 코드는 옮기지 않는다 |
| Simulator → FleetController 참조는 **인터페이스만** 쓴다 (`IPathPlanner`, `IMoveArbiter`) | M3에서 구현체를 HTTP 클라이언트로 바꿔 끼울 때 Simulator 코드가 안 바뀐다 |
| Shared에는 **직렬화 가능한 모델만** 둔다. 알고리즘·I/O 없음 | Shared는 M3부터 5개 서비스가 전부 참조한다. 가벼워야 한다 |
| 디렉터리 이름은 README 구조(`src/Shared`)를 따르고 어셈블리 이름은 `RobotFleet.*` | 경로는 짧게, 네임스페이스는 충돌 없게 |
| 테스트 프레임워크 **xUnit** + `FluentAssertions` | .NET 표준. `Theory`/`InlineData`로 표 형태의 경로 케이스를 돌리기 좋다 |

`Directory.Build.props`

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <LangVersion>12</LangVersion>
  </PropertyGroup>
</Project>
```

패키지: 테스트 프로젝트에만 `xunit`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk`, `FluentAssertions`.
src 프로젝트는 M1에서 **외부 패키지 0개**. (`System.Text.Json`은 BCL 포함)

---

## Shared — 타입 시그니처

### Geometry

```csharp
namespace RobotFleet.Shared.Geometry;

public enum Heading : byte { North = 0, East = 1, South = 2, West = 3 }

public readonly record struct Point(int X, int Y)
{
    public Point Step(Heading h) => h switch
    {
        Heading.North => new(X, Y - 1),     // 03: Y는 아래로 증가
        Heading.East  => new(X + 1, Y),
        Heading.South => new(X, Y + 1),
        Heading.West  => new(X - 1, Y),
        _ => throw new ArgumentOutOfRangeException(nameof(h)),
    };

    public int ManhattanTo(Point other) => Math.Abs(X - other.X) + Math.Abs(Y - other.Y);

    public override string ToString() => $"({X},{Y})";
}

public static class HeadingExtensions
{
    /// <summary>90도 단위 회전 횟수. 0(같음), 1(좌/우), 2(반대).</summary>
    public static int TurnSteps(this Heading from, Heading to)
    {
        var d = Math.Abs((int)from - (int)to) % 4;
        return Math.Min(d, 4 - d);
    }

    public static Heading Opposite(this Heading h) => (Heading)(((int)h + 2) % 4);

    /// <summary>from에서 인접 칸 to로 가는 방향. 인접하지 않으면 예외.</summary>
    public static Heading HeadingTo(Point from, Point to) =>
        (to.X - from.X, to.Y - from.Y) switch
        {
            (0, -1) => Heading.North,
            (1, 0)  => Heading.East,
            (0, 1)  => Heading.South,
            (-1, 0) => Heading.West,
            _ => throw new ArgumentException($"{from} and {to} are not adjacent"),
        };

    public static char ToChar(this Heading h) => "NESW"[(int)h];
}
```

### Map

```csharp
namespace RobotFleet.Shared.Map;

public enum CellType : byte
{
    Blocked = 0,   // 설비·벽
    Aisle   = 1,
    Station = 2,
    Charge  = 3,
    Parking = 4,
    // 5 = OneWay 계열은 M7. M1 로더는 '5'를 만나면 검증 실패로 거부한다.
}

/// <summary>maps/*.json과 1:1 대응하는 DTO. 검증 전 상태.</summary>
public sealed record MapDefinition(
    string MapId,
    int Version,
    int Width,
    int Height,
    double CellSizeMeters,
    IReadOnlyList<string> Grid,
    IReadOnlyList<StationDef> Stations,
    IReadOnlyList<ChargerDef> Chargers,
    IReadOnlyList<ParkingDef> Parkings);

public sealed record StationDef(string Id, int X, int Y, string Type, string EquipmentId);  // Type: "pickup" | "dropoff"
public sealed record ChargerDef(string Id, int X, int Y);
public sealed record ParkingDef(string Id, int X, int Y);

/// <summary>검증을 통과한 불변 맵. 좌표계는 03 문서(원점 좌상단, Y 아래 증가).</summary>
public sealed class GridMap
{
    public string MapId { get; }
    public int Version { get; }
    public int Width { get; }
    public int Height { get; }
    public double CellSizeMeters { get; }
    public IReadOnlyList<StationDef> Stations { get; }
    public IReadOnlyList<ChargerDef> Chargers { get; }
    public IReadOnlyList<ParkingDef> Parkings { get; }

    public CellType this[int x, int y] { get; }          // 범위 밖이면 IndexOutOfRangeException
    public CellType this[Point p] => this[p.X, p.Y];

    public bool InBounds(Point p);
    /// <summary>범위 안이고 Blocked가 아님.</summary>
    public bool IsWalkable(Point p);
    /// <summary>N, E, S, W 순서로 통행 가능한 인접 칸만. 순서가 고정이라 A* 결과가 결정적이다.</summary>
    public IEnumerable<Point> WalkableNeighbors(Point p);

    public StationDef? FindStation(string id);
    public ParkingDef? FindParking(string id);

    /// <summary>검증 후 생성. 실패 시 MapValidationException (모든 오류를 모아서 1회 던짐).</summary>
    public static GridMap FromDefinition(MapDefinition def);
}

public sealed class MapValidationException : Exception
{
    public IReadOnlyList<string> Errors { get; }
    public MapValidationException(IReadOnlyList<string> errors)
        : base($"Map validation failed with {errors.Count} error(s):\n  " + string.Join("\n  ", errors)) => Errors = errors;
}

public static class MapLoader
{
    public static GridMap LoadFromFile(string path);       // FileNotFoundException / JsonException / MapValidationException
    public static GridMap LoadFromJson(string json);
    public static MapDefinition ParseDefinition(string json);   // 검증 없이 DTO만. 테스트용
}
```

JSON 역직렬화 옵션: `PropertyNameCaseInsensitive = true`, 알 수 없는 속성은 무시, 필수 속성 누락은 `JsonException`이 아니라 **검증 오류**로 모은다 (`MapDefinition`의 필드는 전부 nullable로 파싱한 뒤 검증 단계에서 확인).

### 맵 검증 규칙

`FromDefinition`은 아래를 **전부 검사한 뒤** 오류 목록을 한 번에 던진다. 하나 고치면 다음 오류가 나오는 식이면 맵 편집이 괴롭다.

| # | 규칙 | 오류 메시지 형식 |
|---|---|---|
| V1 | `mapId` 비어 있지 않음 | `mapId is required` |
| V2 | `version >= 1` | `version must be >= 1` |
| V3 | `1 <= width <= 500`, `1 <= height <= 500` | `width must be in [1, 500]` |
| V4 | `cellSizeMeters > 0` | `cellSizeMeters must be > 0` |
| V5 | `grid.Count == height` | `grid has {n} rows, expected {height}` |
| V6 | 모든 행 길이 `== width` | `grid row {y} has length {n}, expected {width}` |
| V7 | 모든 문자가 `'0'`~`'4'` | `grid[{y}][{x}] = '{c}' is not a valid cell type (0-4)` |
| V8 | `stations[i]` 좌표가 범위 안이고 그 칸이 `'2'` | `station {id} at ({x},{y}) is not a Station cell` |
| V9 | `grid`의 모든 `'2'` 칸이 `stations`에 존재 | `Station cell ({x},{y}) has no entry in stations` |
| V10 | V8·V9를 `chargers`↔`'3'`, `parkings`↔`'4'`에 동일 적용 | |
| V11 | `stations/chargers/parkings`의 `id` 전체에서 중복 없음 | `duplicate id {id}` |
| V12 | `station.type`은 `"pickup"` 또는 `"dropoff"` | `station {id} has unknown type '{t}'` |
| V13 | 통행 가능 칸이 1개 이상 | `map has no walkable cell` |
| V14 | 통행 가능 칸이 **모두 연결** (BFS) | `{n} walkable cells are unreachable, e.g. ({x},{y})` |

> **V9·V14를 넣는 이유** — 그리드와 목록이 어긋난 맵은 "스테이션으로 가라"는 작업이
> 통로 한가운데를 가리키는 식으로 조용히 틀어진다. 섬이 있는 맵은 A*가 `NoPath`를
> 내는데 알고리즘 버그로 오인하기 쉽다. 둘 다 로더에서 잡는 게 싸다.

### 셀 문자 규약

`grid` 행의 문자 하나가 셀 하나. `x`는 문자 인덱스, `y`는 행 인덱스.

| 문자 | CellType | 통행 | 비고 |
|---|---|---|---|
| `0` | Blocked | ✗ | |
| `1` | Aisle | ○ | |
| `2` | Station | ○ | `stations` 항목 필수 (V8·V9) |
| `3` | Charge | ○ | `chargers` 항목 필수 |
| `4` | Parking | ○ | `parkings` 항목 필수 |
| `5` 이상, 그 외 | — | — | M1 거부 (V7). M7에서 `5`~`8`을 OneWay N/E/S/W로 열 예정 |

### Domain

03 문서의 모델을 그대로 쓰되 M1에 필요한 필드를 추가한다. 03의 `PlannedPath`는 `IReadOnlyList<Point>`였으나
회전 처리를 위해 **`PathStep`(칸 + 진입 방향)** 으로 바꾼다. 03 문서도 이 정의를 따른다.

```csharp
namespace RobotFleet.Shared.Domain;

public enum RobotStatus : byte { Idle = 0, Moving = 1, Waiting = 2, Loading = 3, Unloading = 4, Charging = 5, Error = 6 }
public enum TaskType : byte { Transport = 0, Charge = 1, Park = 2 }
public enum TaskStatus : byte { Pending = 0, Assigned = 1, InProgress = 2, Completed = 3, Failed = 4 }

public sealed class Robot
{
    public required string RobotId { get; init; }        // "R-001"
    public required string MapId { get; init; }
    public Point Position { get; set; }
    public Heading Heading { get; set; }
    public RobotStatus Status { get; set; } = RobotStatus.Idle;
    public string? CurrentTaskId { get; set; }
    public int BatteryPercent { get; set; } = 100;
    public IReadOnlyList<PathStep> PlannedPath { get; set; } = Array.Empty<PathStep>();
    /// <summary>PlannedPath에서 현재 서 있는 칸의 인덱스. 도착하면 PlannedPath.Count - 1.</summary>
    public int PathIndex { get; set; }
}

public sealed class TransportTask
{
    public required string TaskId { get; init; }        // "T-00001"
    public required string MapId { get; init; }
    public TaskType Type { get; init; } = TaskType.Transport;
    public Point Origin { get; init; }
    public Point Destination { get; init; }
    public int Priority { get; init; }                  // 0 = normal, 1 = high
    public DateTimeOffset CreatedAt { get; init; }
    public string? AssignedRobotId { get; set; }
    public TaskStatus Status { get; set; } = TaskStatus.Pending;
}
```

`PathStep`은 FleetController가 아니라 **Shared**에 둔다. `Robot.PlannedPath`가 쓰기 때문.

```csharp
namespace RobotFleet.Shared.Geometry;

/// <summary>경로의 한 칸. Heading은 이 칸에 "진입한 방향"(= 이 칸에서 바라보는 방향).</summary>
public readonly record struct PathStep(Point Cell, Heading Heading);
```

M1의 작업은 `TaskType.Park` 하나다 — "목적지로 가서 멈춘다". `Origin`은 시작 칸, `Destination`은 목적지.
Transport(적재→하역)는 M2.

---

## FleetController — 경로 계획

### 비용 상수

```csharp
namespace RobotFleet.FleetController.Planning;

public sealed record PathCost(double Move = 1.0, double TurnPer90 = 0.5)
{
    public static readonly PathCost Default = new();

    /// <summary>from을 보고 있다가 to 방향으로 돌리는 비용. 180도 = 2 × TurnPer90 = 1.0</summary>
    public double Turn(Heading from, Heading to) => from.TurnSteps(to) * TurnPer90;
}
```

| 항목 | 값 | 근거 |
|---|---|---|
| 이동 1칸 | 1.0 | 기준 단위. 1000ms에 대응 |
| 회전 90도 | 0.5 | 300ms ÷ 1000ms = 0.3이지만 04 문서 값 0.5를 유지. 비용을 시간보다 조금 무겁게 잡아 지그재그를 더 억제한다. 실험 변수(M7)이므로 `PathCost`로 주입 가능하게 |
| 회전 180도 | 1.0 | 제자리에서 두 번 꺾는다. 별도 금지 없음 — 막다른 길에서 돌아 나오려면 필요하다 |
| 휴리스틱 | `ManhattanTo × Move` | 회전 비용은 ≥ 0이라 맨해튼 거리가 여전히 **하한**(admissible) |

### 결과 타입

```csharp
public enum PathStatus : byte
{
    Found = 0,
    AlreadyAtGoal = 1,   // start == goal. Steps = [ (start, startHeading) ], Cost = 0
    NoPath = 2,          // 둘 다 유효하지만 연결되지 않음
    InvalidStart = 3,    // 범위 밖 또는 Blocked — 로봇 상태가 깨진 것. 호출자가 Error 처리
    InvalidGoal = 4,     // 범위 밖 또는 Blocked — 작업 정의가 깨진 것. 호출자가 Failed 처리
}

public sealed record PathResult
{
    public required PathStatus Status { get; init; }
    /// <summary>Steps[0] = (start, startHeading). 마지막 = goal. Found/AlreadyAtGoal 외에는 빈 배열.</summary>
    public required IReadOnlyList<PathStep> Steps { get; init; }
    public required double Cost { get; init; }
    public required int ExpandedNodes { get; init; }     // 성능 테스트·메트릭용
    public TimeSpan Elapsed { get; init; }

    public bool IsSuccess => Status is PathStatus.Found or PathStatus.AlreadyAtGoal;
    /// <summary>이동 칸 수 = Steps.Count - 1. 03의 plannedPathLength가 이 값.</summary>
    public int MoveCount => Math.Max(0, Steps.Count - 1);

    public static PathResult Failure(PathStatus status, int expanded, TimeSpan elapsed);
}
```

> **`NoPath`와 `InvalidGoal`을 나누는 이유** — 처방이 다르다. `NoPath`는 맵이 일시적으로
> 막혔거나(M2 재계획 대상) 맵 설계 문제고, `InvalidGoal`은 작업 생성 버그다.
> 03의 `TaskFailed.reason`에서 `no_path`로 합쳐 내보내더라도 로그에는 구분이 남아야 한다.

### 인터페이스와 구현

```csharp
public interface IPathPlanner
{
    PathResult FindPath(GridMap map, Point start, Heading startHeading, Point goal);
}

public sealed class AStarPathPlanner : IPathPlanner
{
    public AStarPathPlanner(PathCost? cost = null);     // null → PathCost.Default
    public PathResult FindPath(GridMap map, Point start, Heading startHeading, Point goal);
}
```

### A* 규약 — 구현자가 지켜야 할 것

| 항목 | 규약 |
|---|---|
| 노드 | `(x, y, heading)`. 정수 id = `(y * Width + x) * 4 + heading`. 배열 `g[]`, `parent[]`, `closed[]` 크기 `Width × Height × 4` (60×40이면 9600) |
| 목표 | `goal` 칸의 **4개 노드 중 아무거나** 먼저 꺼내지면 종료. 도착 방향은 묻지 않는다 |
| 이웃 확장 | 현재 `(p, h)`에서 `d ∈ {N, E, S, W}` 순서로 `p.Step(d)`가 `IsWalkable`이면 후보. 비용 `g + cost.Turn(h, d) + cost.Move`, 새 노드 `(p.Step(d), d)` |
| 180도 회전 | 별도 노드 없음. `d = h.Opposite()`인 이웃으로 가면 자연히 `Turn = 1.0`이 붙는다 |
| 우선순위 큐 | `PriorityQueue<int, (double f, double h, long seq)>`. 동점은 **h 작은 쪽, 그다음 삽입 순서**. 결정적이어야 테스트가 경로를 문자 그대로 비교할 수 있다 |
| 닫힌 노드 재방문 | 무시 (consistent 휴리스틱이므로 안전) |
| 경계 케이스 순서 | ① `!InBounds(start) \|\| !IsWalkable(start)` → `InvalidStart` ② goal 동일 검사 → `InvalidGoal` ③ `start == goal` → `AlreadyAtGoal` ④ 탐색 |
| 경로 복원 | `parent[]`를 goal 노드에서 거슬러 올라 뒤집는다. `Steps[0].Heading == startHeading` 보장 |
| 할당 | 핫 루프에서 LINQ·클로저 금지. 배열은 호출마다 새로 잡아도 된다(9600 × 3 배열 = 수십 KB). M2에서 풀링 검토 |

성능 목표: 60×40 맵에서 `FindPath` 1회 **< 2ms** (Release). 테스트에 포함.

---

## FleetController — 이동 승인 (M2+ 확장 지점)

M1에서는 항상 승인하지만, **Simulator가 이 인터페이스를 거쳐서만 움직이게** 한다.
M2에서 메모리 셀 점유 판정, M4에서 Redis 락, M3에서 HTTP 클라이언트로 구현체만 바꾼다.

```csharp
namespace RobotFleet.FleetController.Arbitration;

public readonly record struct MoveRequest(
    string RobotId, Point From, Point To, Heading Heading, long Tick,
    int WaitMs = 0, int RemainingSteps = 0, int TaskPriority = 0);   // 뒤 셋은 01 배치 API의 우선순위 입력

public enum MoveDenyReason : byte { None = 0, Occupied = 1, Swap = 2, Deadlock = 3, StateLost = 4 }

public readonly record struct MoveDecision(
    string RobotId, bool Granted, MoveDenyReason Reason = MoveDenyReason.None,
    string? BlockedBy = null, Point? YieldTo = null)
{
    public static MoveDecision Grant(string robotId) => new(robotId, true);
}

public interface IMoveArbiter
{
    /// <summary>한 틱의 이동 요청 전부. 01의 배치 API와 1:1. 응답 순서는 요청 순서와 같다.</summary>
    ValueTask<IReadOnlyList<MoveDecision>> DecideAsync(
        string mapId, long tick, IReadOnlyList<MoveRequest> requests, CancellationToken ct = default);

    /// <summary>이동 완료 후 이전 칸 해제. M1 구현은 no-op.</summary>
    ValueTask ReleaseAsync(string mapId, string robotId, Point cell, CancellationToken ct = default);
}

public sealed class AlwaysGrantMoveArbiter : IMoveArbiter
{
    // DecideAsync → requests.Select(r => MoveDecision.Grant(r.RobotId)) 를 배열로
    // ReleaseAsync → ValueTask.CompletedTask
}
```

| 결정 | 근거 |
|---|---|
| 단건이 아니라 **배치 시그니처** | 01에서 틱당 배치 1건으로 결정했다. M1부터 배치로 호출해야 M3에서 HTTP로 바꿀 때 루프 구조가 안 바뀐다 |
| `ValueTask` | M1은 동기 완료, M3는 네트워크. 호출부는 처음부터 `await` |
| `MoveRequest`에 우선순위 입력 필드 포함 (M1은 0) | 필드 추가가 아니라 값 채우기만으로 M2 전환 |
| `mapId`를 인자로 | 라이브러리가 맵 하나에 묶이지 않게. M4 Redis 키에 들어간다 |

---

## Simulator — 틱 루프

### 설정

```csharp
public sealed record SimulationOptions
{
    public required string MapPath { get; init; }
    public Point Start { get; init; }                // 기본: 첫 번째 Parking
    public Heading StartHeading { get; init; } = Heading.North;
    public required Point Goal { get; init; }
    public int TickMs { get; init; } = 100;
    public double SpeedFactor { get; init; } = 1.0;  // 실시간 대기 = TickMs / SpeedFactor. 0이면 대기 없음
    public int MoveMsPerCell { get; init; } = 1000;  // 1 m/s
    public int TurnMsPer90 { get; init; } = 300;     // 03 문서
    public int RenderEveryTicks { get; init; } = 5;  // 맵 그리기 주기. 0이면 안 그림
    public long MaxTicks { get; init; } = 100_000;   // 안전장치
}
```

명령행: `--map <path> --start x,y --goal x,y [--heading N|E|S|W] [--speed 1.0] [--tick 100] [--render 5] [--max-ticks N]`
`--start`를 생략하면 맵의 첫 Parking, `--goal`을 생략하면 맵의 첫 Station.

### 로봇 동작 모델

로봇은 한 번에 **하나의 동작**(회전 또는 이동)을 수행하며, 동작에는 남은 시간이 있다.

```csharp
public enum RobotActionKind : byte { None = 0, Turn = 1, Move = 2 }

public sealed class RobotAction
{
    public RobotActionKind Kind { get; init; }
    public Heading TargetHeading { get; init; }   // Turn
    public Point TargetCell { get; init; }        // Move
    public int RemainingMs { get; set; }
}
```

### 틱 처리 순서 (로봇 1대)

`RobotStepper.StepAsync(robot, tick)`이 **매 틱 아래 순서를 정확히** 수행한다. 한 틱에 동작 시작은 최대 1회.

```
1. [계획]  Status == Idle && 작업 있음 && PlannedPath 비어 있음
            → planner.FindPath(map, Position, Heading, task.Destination)
            → IsSuccess: PlannedPath = Steps, PathIndex = 0, Status = Moving (RobotStatusChanged: task_assigned)
            → 실패:     Status = Error, task.Status = Failed, 루프 종료 (exit code 2)
            (이 틱에서 2단계로 계속 진행)

2. [시작]  CurrentAction == None && Status == Moving
            → PathIndex == PlannedPath.Count - 1
                → 도착. Status = Idle, task.Status = Completed, PlannedPath 비움 (RobotStatusChanged: arrived_park)
                  → 이 틱에서 루프 종료 (3단계 없음)
            → next = PlannedPath[PathIndex + 1]
                → Heading != next.Heading
                    → CurrentAction = Turn(next.Heading, Heading.TurnSteps(next.Heading) × TurnMsPer90 + carry)
                → else
                    → decision = arbiter.DecideAsync(mapId, tick, [MoveRequest(...)])[0]
                    → Granted:  CurrentAction = Move(next.Cell, MoveMsPerCell + carry)
                    → 거부:     Status = Waiting (M1에선 발생하지 않음. M2 처리). 3단계 없음

3. [진행]  CurrentAction != None   (이 틱에 방금 시작한 동작도 포함)
            → RemainingMs -= TickMs
            → RemainingMs <= 0 이면 완료 처리:
                Turn: Heading = TargetHeading
                Move: arbiter.ReleaseAsync(이전 칸); Position = TargetCell; PathIndex++
              carry = RemainingMs (0 또는 음수), CurrentAction = None
            (완료되어도 이 틱에서는 새 동작을 시작하지 않는다 — 다음 틱의 2단계가 시작한다)
```

| 결정 | 근거 |
|---|---|
| 순서가 **시작 → 진행**이라 시작 틱에도 시간이 흐른다 | "동작 하나 = 1000ms = 정확히 10틱"이 되어 테스트에서 틱 수를 손으로 셀 수 있다. 진행 → 시작 순서면 11틱이 되어 1000ms 동작이 1100ms 걸린다 |
| 완료 틱에는 새 동작을 시작하지 않는다 | 도착 판정이 완료 다음 틱에 일어나 "도착 틱 = 마지막 완료 틱 + 1"로 규칙이 단순하다. M2에서 배치 요청을 틱 시작에 모아 1회 보내는 구조와도 맞다 |
| `carry`(잔여 음수)를 다음 동작에 더한다 | 1000·300이 100의 배수라 M1에선 항상 0이지만, `--tick 150` 같은 설정에서 시간이 새지 않게 |
| 회전은 arbiter를 거치지 않는다 | 제자리 동작은 셀 점유가 바뀌지 않는다. 01의 배치 요청에도 회전은 없다 |
| 2칸 선점(04)은 M2 | M1 arbiter는 no-op. 단 `ReleaseAsync(이전 칸)` 호출 위치는 M1에 넣어 둔다 |

**예상 틱 수** (이 규칙으로 손으로 세는 법, 테스트 기대값의 근거)

```
직진 3칸, 회전 없음:
  tick 1: 계획 + Move 시작(1000→900)   tick 10: (1,0) 도착, 동작 없음
  tick 11: Move 시작                    tick 20: (2,0)
  tick 21: Move 시작                    tick 30: (3,0)
  tick 31: 도착 판정 → Idle
  → 31틱

시작 heading North, 동쪽 3칸:
  tick 1: 계획 + Turn 시작(300→200)    tick 3: heading East
  tick 4: Move 시작                     tick 13: (1,0)
  tick 14 / 23: (2,0)   tick 24 / 33: (3,0)   tick 34: Idle
  → 34틱
```

### 루프

```csharp
public interface ITickDelay { Task DelayAsync(int ms, CancellationToken ct); }
public sealed class RealTimeDelay : ITickDelay   // Task.Delay
public sealed class NoDelay : ITickDelay         // Task.CompletedTask (테스트·--speed 0)

public sealed class SimulationStats
{
    public long Tick { get; set; }
    public int OverrunCount { get; set; }
    public long TotalMoveMs, TotalTurnMs;      // 05 시간 분해의 씨앗. M1은 Move만 의미 있음
    public TimeSpan WallClock { get; set; }
}

public sealed class SimulationLoop
{
    public SimulationLoop(GridMap map, Robot robot, TransportTask task, IPathPlanner planner,
                          IMoveArbiter arbiter, SimulationOptions options, ITickDelay delay, ConsoleRenderer? renderer);

    /// <summary>한 틱만 진행. 테스트가 직접 호출한다.</summary>
    public ValueTask<bool> StepOnceAsync(CancellationToken ct = default);   // false = 종료 조건 도달

    /// <summary>종료 조건(도착 / Error / MaxTicks / 취소)까지 실행. 반환값 = 종료 코드.</summary>
    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            var sw = Stopwatch.StartNew();
            var @continue = await StepOnceAsync(ct);   // stats.Tick++ 와 renderer.OnTick 은 StepOnceAsync 안에서
            if (!@continue) break;

            if (options.SpeedFactor > 0)
            {
                var budget = options.TickMs / options.SpeedFactor;
                var wait = budget - sw.Elapsed.TotalMilliseconds;
                if (wait > 0) await delay.DelayAsync((int)wait, ct);
                else stats.OverrunCount++;         // 01: 틱 초과는 M1부터 센다
            }
        }
        return ExitCode();   // 0 도착, 2 경로 없음/Error, 3 MaxTicks 초과, 130 취소
    }
}
```

`StepOnceAsync` 안에서 틱을 올리고 `RobotStepper.StepAsync`를 호출하고, M2에서는 "모든 로봇의 요청 수집 → `DecideAsync` 1회 → 각 로봇 적용"으로 바뀐다.
M1은 로봇 1대라 `RobotStepper`가 직접 `DecideAsync`를 부르지만, **요청 생성과 결정 적용을 별도 메서드**로 나눠 둔다:

```csharp
public MoveRequest? BuildMoveRequest(Robot robot, long tick);                // 2단계 "else" 분기까지 (회전·도착이면 null)
public ValueTask ApplyAsync(Robot robot, long tick, MoveDecision? decision); // 나머지 (락 해제가 비동기라 ValueTask)
```

### 콘솔 출력 형식

**이벤트 로그** (상태·동작이 바뀔 때만 한 줄). 포맷을 고정해 테스트가 문자열로 검증한다.

```
[t=000001 0.1s] R-001 PLAN    (0,39)N → (6,3)   steps=42 cost=43.0 expanded=200 in 0.8ms
[t=000001 0.1s] R-001 MOVE    (0,39) → (0,38)
[t=000011 1.1s] R-001 MOVE    (0,38) → (0,37)
...
[t=000341 34.1s] R-001 TURN    N → E
[t=000344 34.4s] R-001 MOVE    (0,5) → (1,5)
...
[t=000427 42.7s] R-001 ARRIVED (6,3)   ticks=427 moves=42 turns=2 overrun=0
```

| 필드 | 규칙 |
|---|---|
| `t=` | 틱 번호 6자리 0 채움, 그 뒤 논리 시각 `tick × TickMs / 1000` 초 (소수 1자리) |
| 동작 | `PLAN` `MOVE` `TURN` `ARRIVED` `NOPATH` `WAIT` 를 7자 폭으로 좌정렬 |
| 좌표 | `(x,y)`, 방향은 `N/E/S/W` 한 글자 |

**맵 렌더** (`RenderEveryTicks`마다, `--render 0`이면 생략). 60×40이면 터미널을 넘기므로 로봇 중심 **21×11 창**만 그린다.

```
  ┌───────────────────────┐
  │ . . . . # # # # . . . │     .  통로        #  설비
  │ . . . . # # # # . . . │     S  Station     C  Charge      P  Parking
  │ . . . . . . . . . . . │     ^ > v <  로봇 (방향)
  │ . . . . . ^ . . . . . │     *  남은 경로
  │ . . . . . * . . . . . │
  └───────────────────────┘
  tick 000354  pos (1,5) E  Moving  path 36/42
```

콘솔 지우기는 하지 않는다(CI 로그에 ANSI 제어 문자가 섞이지 않게). 창을 매번 아래에 덧붙인다.

---

## 샘플 맵

### maps/factory-a.json

03 문서 지침대로 만들었다. 60×40, 주통로 2칸(가로 y=0-1·19-20·38-39, 세로 x=0-1·29-30·58-59),
사분면 내부는 **1칸 지선**(세로 5칸 간격, 가로 4칸 간격)으로 4×3 설비 블록을 가른다.
스테이션 16개는 전부 1칸 지선 위에 있어 M2에서 교착을 유발한다. 통행 가능 1256칸, 전부 연결(V14 통과).

```json
{
  "mapId": "factory-a",
  "version": 1,
  "width": 60,
  "height": 40,
  "cellSizeMeters": 1.0,
  "grid": [
    "111111111111111111111111111111111111111111111111111111111111",
    "111111111111111111111111111111111111111111111111111111111111",
    "110000100001000010000100001001100001000010000100001000010011",
    "110000200001000020000100001001100002000010000200001000010011",
    "110000100001000010000100001001100001000010000100001000010011",
    "111111111111111111111111111111111111111111111111111111111111",
    "110000100001000010000100001001100001000010000100001000010011",
    "110000100001000010000100001001100001000010000100001000010011",
    "110000100001000010000100001001100001000010000100001000010011",
    "111111111111111111111111111111111111111111111111111111111111",
    "110000100001000010000100001001100001000010000100001000010011",
    "110000100001000010000100001001100001000010000100001000010011",
    "110000100001000010000100001001100001000010000100001000010011",
    "111111211111111111111211111111111112111111111111112111111111",
    "110000100001000010000100001001100001000010000100001000010011",
    "110000100001000010000100001001100001000010000100001000010011",
    "110000100001000010000100001001100001000010000100001000010011",
    "111111111111111111111111111111111111111111111111111111111111",
    "110000100001000010000100001001100001000010000100001000010011",
    "111111111111111111111111111111111111111111111111111111111111",
    "111111111111111111111111111111111111111111111111111111111111",
    "110000100001000010000100001001100001000010000100001000010011",
    "110000200001000020000100001001100002000010000200001000010011",
    "110000100001000010000100001001100001000010000100001000010011",
    "111111111111111111111111111111111111111111111111111111111111",
    "110000100001000010000100001001100001000010000100001000010011",
    "110000100001000010000100001001100001000010000100001000010011",
    "110000100001000010000100001001100001000010000100001000010011",
    "111111111111111111111111111111111111111111111111111111111111",
    "110000100001000010000100001001100001000010000100001000010011",
    "110000100001000010000100001001100001000010000100001000010011",
    "110000100001000010000100001001100001000010000100001000010011",
    "111111211111111111111211111111111112111111111111112111111111",
    "110000100001000010000100001001100001000010000100001000010011",
    "110000100001000010000100001001100001000010000100001000010011",
    "110000100001000010000100001001100001000010000100001000010011",
    "111111111111111111111111111111111111111111111111111111111111",
    "110000100001000010000100001001100001000010000100001000010011",
    "111111111111111111111111111111111111111111111111111111111133",
    "444444444411111111111111111111111111111111111111111111111133"
  ],
  "stations": [
    {"id": "ST-01", "x": 6,  "y": 3,  "type": "pickup",  "equipmentId": "EQ-01"},
    {"id": "ST-02", "x": 16, "y": 3,  "type": "dropoff", "equipmentId": "EQ-02"},
    {"id": "ST-03", "x": 6,  "y": 13, "type": "pickup",  "equipmentId": "EQ-03"},
    {"id": "ST-04", "x": 21, "y": 13, "type": "dropoff", "equipmentId": "EQ-04"},
    {"id": "ST-05", "x": 35, "y": 3,  "type": "pickup",  "equipmentId": "EQ-05"},
    {"id": "ST-06", "x": 45, "y": 3,  "type": "dropoff", "equipmentId": "EQ-06"},
    {"id": "ST-07", "x": 35, "y": 13, "type": "pickup",  "equipmentId": "EQ-07"},
    {"id": "ST-08", "x": 50, "y": 13, "type": "dropoff", "equipmentId": "EQ-08"},
    {"id": "ST-09", "x": 6,  "y": 22, "type": "pickup",  "equipmentId": "EQ-09"},
    {"id": "ST-10", "x": 16, "y": 22, "type": "dropoff", "equipmentId": "EQ-10"},
    {"id": "ST-11", "x": 6,  "y": 32, "type": "pickup",  "equipmentId": "EQ-11"},
    {"id": "ST-12", "x": 21, "y": 32, "type": "dropoff", "equipmentId": "EQ-12"},
    {"id": "ST-13", "x": 35, "y": 22, "type": "pickup",  "equipmentId": "EQ-13"},
    {"id": "ST-14", "x": 45, "y": 22, "type": "dropoff", "equipmentId": "EQ-14"},
    {"id": "ST-15", "x": 35, "y": 32, "type": "pickup",  "equipmentId": "EQ-15"},
    {"id": "ST-16", "x": 50, "y": 32, "type": "dropoff", "equipmentId": "EQ-16"}
  ],
  "chargers": [
    {"id": "CH-01", "x": 58, "y": 38},
    {"id": "CH-02", "x": 59, "y": 38},
    {"id": "CH-03", "x": 58, "y": 39},
    {"id": "CH-04", "x": 59, "y": 39}
  ],
  "parkings": [
    {"id": "PK-01", "x": 0, "y": 39}, {"id": "PK-02", "x": 1, "y": 39},
    {"id": "PK-03", "x": 2, "y": 39}, {"id": "PK-04", "x": 3, "y": 39},
    {"id": "PK-05", "x": 4, "y": 39}, {"id": "PK-06", "x": 5, "y": 39},
    {"id": "PK-07", "x": 6, "y": 39}, {"id": "PK-08", "x": 7, "y": 39},
    {"id": "PK-09", "x": 8, "y": 39}, {"id": "PK-10", "x": 9, "y": 39}
  ]
}
```

M1 기본 시나리오 `PK-01 (0,39) N → ST-01 (6,3)`: 경로 42칸, 회전 2회(N→E, E→N), 비용 43.0.
틱 수 = 42 × 10 + 2 × 3 = 426, 도착 판정은 다음 틱이므로 **427틱**.

### maps/test-corridor.json

9×7. 위·아래 두 공간을 **(4,3) 한 칸**이 잇는다. A* 테스트와 M2 교착 테스트가 같이 쓴다.

```json
{
  "mapId": "test-corridor",
  "version": 1,
  "width": 9,
  "height": 7,
  "cellSizeMeters": 1.0,
  "grid": [
    "111111111",
    "120000021",
    "111111111",
    "000010000",
    "111111111",
    "130000041",
    "111111111"
  ],
  "stations": [
    {"id": "ST-01", "x": 1, "y": 1, "type": "pickup",  "equipmentId": "EQ-01"},
    {"id": "ST-02", "x": 7, "y": 1, "type": "dropoff", "equipmentId": "EQ-02"}
  ],
  "chargers": [ {"id": "CH-01", "x": 1, "y": 5} ],
  "parkings": [ {"id": "PK-01", "x": 7, "y": 5} ]
}
```

---

## 테스트 목록

테스트 안에서 작은 맵은 `MapDefinition`을 코드로 만든다 (`TestMaps.Open(5,5)`, `TestMaps.FromRows(...)`).
파일 맵은 `maps/`를 테스트 프로젝트 출력으로 복사(`<Content Include="../../maps/*.json" CopyToOutputDirectory="PreserveNewest" />`).

### Shared.Tests

| ID | 테스트 | 입력 | 기대 |
|---|---|---|---|
| S-01 | `Point.Step` 4방향 | `(5,5)` × N/E/S/W | `(5,4)` `(6,5)` `(5,6)` `(4,5)` |
| S-02 | `TurnSteps` | (N,N) (N,E) (N,W) (N,S) (E,W) (W,N) | 0, 1, 1, 2, 2, 1 |
| S-03 | `HeadingTo` | `(3,3)→(3,2)`, `(3,3)→(4,3)`, `(3,3)→(5,3)` | North, East, `ArgumentException` |
| S-04 | 로더 정상 | `maps/factory-a.json` | `Width 60, Height 40, Stations.Count 16, Chargers 4, Parkings 10`, `this[6,3] == Station`, `this[2,2] == Blocked` |
| S-05 | 로더 정상 | `maps/test-corridor.json` | `this[4,3] == Aisle`, `this[3,3] == Blocked`, `IsWalkable((4,3))` true |
| S-06 | V5 행 수 불일치 | `height 3`, `grid` 2행 | `MapValidationException`, `Errors` 에 `"grid has 2 rows, expected 3"` |
| S-07 | V6 행 길이 불일치 | `width 5`, 2행째 길이 4 | `Errors` 에 `"grid row 1 has length 4, expected 5"` |
| S-08 | V7 잘못된 문자 | 셀에 `'5'`, 다른 셀에 `'x'` | 오류 2건, 각각 좌표 포함 |
| S-09 | V8 스테이션 칸 불일치 | `stations=[(2,2)]`, `grid[2][2]='1'` | `"station ST-01 at (2,2) is not a Station cell"` |
| S-10 | V9 목록 누락 | `grid[1][1]='2'`, `stations=[]` | `"Station cell (1,1) has no entry in stations"` |
| S-11 | V11 id 중복 | station `ST-01` 2개 | `"duplicate id ST-01"` |
| S-12 | V14 섬 | `"11011","11011","11011"` (가운데 열 막힘) | `Errors` 에 `"... unreachable ..."` 1건 |
| S-13 | 오류 누적 | V5·V7·V11 동시 위반 | `Errors.Count == 3` (한 번에 전부) |
| S-14 | 필수 필드 누락 | `mapId` 없음 | `MapValidationException` (JsonException 아님) |
| S-15 | `WalkableNeighbors` 순서 | open 5×5, `(2,2)` | 정확히 `[(2,1),(3,2),(2,3),(1,2)]` 순서 |
| S-16 | `WalkableNeighbors` 경계 | open 5×5, `(0,0)` | `[(1,0),(0,1)]` |

### FleetController.Tests — A*

`cost = PathCost.Default`. "경로" 열은 `Steps`를 `(x,y)H` 로 적은 것. 전부 **문자 그대로 일치**해야 한다(결정적 tie-break).

| ID | 테스트 | 맵 / 입력 | 기대 |
|---|---|---|---|
| P-01 | 직진 | corridor, `(0,0) E → (8,0)` | Found, Cost 8.0, 9 steps, 전부 heading E |
| P-02 | 회전 1회 선호 | open 5×5, `(0,0) E → (4,4)` | Found, Cost **8.5**, 경로 `(0,0)E (1,0)E (2,0)E (3,0)E (4,0)E (4,1)S (4,2)S (4,3)S (4,4)S` |
| P-03 | 시작 방향이 다르면 초기 회전 | open 5×5, `(0,0) N → (4,4)` | Cost **9.0**, 경로는 P-02와 동일하되 `Steps[0] = (0,0)N` |
| P-04 | 180도 회전 | open 5×5, `(2,0) W → (4,0)` | Cost **3.0** (1.0 + 2), 경로 `(2,0)W (3,0)E (4,0)E` |
| P-05 | 1칸 통로 통과 | corridor, `(1,1) S → (7,5)` | Cost **12.0**, 11 steps, 경로 `(1,1)S (1,2)S (2,2)E (3,2)E (4,2)E (4,3)S (4,4)S (5,4)E (6,4)E (7,4)E (7,5)S` |
| P-06 | 경로 없음 | `"11011","11011","11011"` 를 **검증 없이** `GridMap` 생성(`TestMaps.Unchecked`), `(0,0) E → (4,0)` | `NoPath`, `Steps` 비어 있음, `0 < ExpandedNodes <= 24` (도달 가능한 6칸 × 4방향이 상한) |
| P-07 | 같은 지점 | open 5×5, `(2,2) E → (2,2)` | `AlreadyAtGoal`, `Steps == [(2,2)E]`, Cost 0, `MoveCount 0` |
| P-08 | 시작점 막힘 | corridor, `(3,3) N → (0,0)` | `InvalidStart`, Steps 비어 있음 |
| P-09 | 시작점 범위 밖 | corridor, `(-1,0)` | `InvalidStart` |
| P-10 | 목적지 막힘 | corridor, `(0,0) E → (3,3)` | `InvalidGoal` |
| P-11 | 목적지 범위 밖 | corridor, `(0,0) E → (9,0)` | `InvalidGoal` |
| P-12 | 시작·목적지 둘 다 무효 | corridor, `(3,3) → (2,3)` | `InvalidStart` (시작 먼저) |
| P-13 | 회전 비용 0이면 지그재그 허용 | open 5×5, `PathCost(TurnPer90: 0)`, `(0,0) E → (4,4)` | Cost 8.0, `MoveCount 8` (경로 문자열은 검사하지 않음 — 동점 많음) |
| P-14 | 회전 비용이 거리와 교환됨 | corridor, `(0,2) E → (8,2)` | Cost 8.0 직진 (y=2 통로). 위·아래로 돌아가는 더 긴 경로를 고르지 않음 |
| P-15 | 경로 유효성 (속성 테스트) | factory-a, 모든 `(Parking, Station)` 쌍 160개 | 전부 Found; 연속 칸이 인접; 모든 칸 `IsWalkable`; `Steps[i].Heading == HeadingTo(Steps[i-1].Cell, Steps[i].Cell)` (i ≥ 1); `Cost == Σ(Move + Turn)` 재계산값과 1e-9 이내 |
| P-16 | 비용 하한 | P-15의 각 결과 | `Cost >= ManhattanTo × 1.0` |
| P-17 | 기본 시나리오 | factory-a, `(0,39) N → (6,3)` | Cost **43.0**, 43 steps, 회전 2회, `Steps[^1].Cell == (6,3)` |
| P-18 | 성능 | factory-a, `(0,39) N → (59,0)` 100회 | 평균 `< 2ms` (Release; Debug에서는 `< 10ms`로 완화) |
| P-19 | 결정성 | P-17을 2회 | 두 `Steps` 가 완전 동일 |

### FleetController.Tests — Arbiter

| ID | 테스트 | 입력 | 기대 |
|---|---|---|---|
| A-01 | 항상 승인 | 요청 3건 | 응답 3건, 전부 `Granted`, `RobotId` 순서 보존 |
| A-02 | 빈 배치 | 요청 0건 | 빈 응답, 예외 없음 |

### Simulator.Tests

`NoDelay`, `renderer = null`, open 5×5 또는 corridor.

| ID | 테스트 | 입력 | 기대 |
|---|---|---|---|
| T-01 | 직진 3칸 틱 수 | open 5×5, `(0,0) E → (3,0)` | `StepOnceAsync` 30회 후 `Position (3,0)`, `Status Moving`; 31회째 `false` 반환, `Status Idle`, `task.Status Completed`, `stats.Tick == 31` |
| T-02 | 초기 회전 포함 | open 5×5, `(0,0) N → (3,0)` | tick 3 끝에 `Heading East`, tick 13 끝에 `(1,0)`, 34회째 종료 |
| T-03 | 180도 회전 시간 | open 5×5, `(2,0) W → (4,0)` | tick 6 끝에 `Heading East` (600ms), 27회째 종료 (6 + 10 + 10 + 1) |
| T-04 | 중간 회전 | open 5×5, `(0,0) E → (1,1)` | 경로 `(0,0)E (1,0)E (1,1)S`. tick 10 `(1,0)`, tick 11~13 Turn, tick 14 Move 시작, tick 23 `(1,1)`, 24회째 종료 |
| T-05 | 잔여 시간 이월 | `TickMs 150`, open 5×5, `(0,0) E → (2,0)` | tick 7 끝에 `(1,0)` (1000 − 7×150 = −50, carry −50); tick 8에 두 번째 Move가 950ms로 시작; tick 14 끝에 `(2,0)` (950 − 7×150 = −100); 15회째 종료 |
| T-06 | 경로 없음 | P-06 맵, `(0,0) → (4,0)` | 1회째 `false`, `Status Error`, `task.Status Failed`, `RunAsync` 반환 2 |
| T-07 | 같은 지점 | `(2,2) → (2,2)` | 1회째: PLAN 후 2단계에서 즉시 ARRIVED, `false`, Idle, exit 0 |
| T-08 | MaxTicks | `MaxTicks 5`, 긴 경로 | 5회 후 종료, exit 3, `Status Moving` 유지 |
| T-09 | arbiter 호출 횟수 | 직진 3칸, 기록용 fake arbiter | `DecideAsync` 3회 (이동마다 1회, 회전·도착 틱엔 0회), `ReleaseAsync` 3회, 각 Release의 `cell`은 이전 칸 |
| T-10 | arbiter 거부 시 Waiting | 항상 거부하는 fake | tick 1 후 `Status Waiting`, 위치 불변. (M1에선 루프가 계속 Waiting으로 돌다 MaxTicks로 종료 — exit 3) |
| T-11 | 콘솔 로그 포맷 | `StringWriter` renderer, T-01 | 첫 줄 `"[t=000001 0.1s] R-001 PLAN    (0,0)E → (3,0)   steps=3 cost=3.0 ..."` 로 시작, 마지막 줄이 `"ARRIVED (3,0)"` 포함, `MOVE` 줄 정확히 3개 |
| T-12 | 틱 초과 계수 | `SpeedFactor 1`, `TickMs 1`, 가짜 delay | `OverrunCount > 0` (예산 1ms를 계산이 넘김) |
| T-13 | 통합 — 기본 시나리오 | factory-a, `(0,39) N → (6,3)`, `SpeedFactor 0` | exit 0, `stats.Tick == 427`, 최종 `(6,3)` Heading North |

T-05의 숫자는 구현자가 규칙대로 다시 세어 검증한다. 기대값이 틀렸다고 판단되면 **문서를 고치고** 테스트를 맞춘다 (규칙이 우선).

---

## 작업 분할 체크리스트

`[단순]` = DTO·로더·보일러플레이트·출력. 규칙이 표로 다 정해져 있어 그대로 옮기면 된다.
`[핵심]` = 알고리즘·시간 처리. 기대값 표와 맞춰 가며 구현한다.

**0. 골격**
- [ ] [단순] `RobotFleet.sln`, `Directory.Build.props`, 6개 csproj, 참조 관계, `.gitignore` 확인
- [ ] [단순] 테스트 패키지 추가, `maps/*.json` 테스트 출력 복사 설정, `dotnet test` 빈 통과

**1. Shared**
- [ ] [단순] `Heading`, `Point`, `HeadingExtensions`, `PathStep` + S-01~S-03
- [ ] [단순] `CellType`, `MapDefinition`·`StationDef`·`ChargerDef`·`ParkingDef`, `MapValidationException`
- [ ] [단순] `MapLoader.ParseDefinition / LoadFromJson / LoadFromFile` (nullable DTO 파싱)
- [ ] [단순] `GridMap` 저장 구조·인덱서·`InBounds`·`IsWalkable`·`WalkableNeighbors`(N,E,S,W 고정) + S-15, S-16
- [ ] [핵심] `GridMap.FromDefinition` 검증 V1~V13 (오류 누적) + S-04~S-11, S-13, S-14
- [ ] [핵심] V14 연결성 BFS + S-12
- [ ] [단순] `Robot`, `RobotStatus`, `TransportTask`, `TaskType`, `TaskStatus`

**2. FleetController**
- [ ] [단순] `PathCost`, `PathStatus`, `PathResult`, `IPathPlanner`
- [ ] [단순] `MoveRequest`, `MoveDecision`, `MoveDenyReason`, `IMoveArbiter`, `AlwaysGrantMoveArbiter` + A-01, A-02
- [ ] [핵심] `AStarPathPlanner` — 노드 인코딩, 배열 기반 open/closed, 결정적 tie-break, 경계 케이스 순서 + P-01~P-14
- [ ] [핵심] 경로 복원·비용 재계산 검증 + P-15~P-19 (성능 포함)

**3. Simulator**
- [ ] [단순] `SimulationOptions` + 명령행 파싱 (`--map --start --goal --heading --speed --tick --render --max-ticks`), 기본값(첫 Parking/첫 Station)
- [ ] [단순] `ITickDelay`, `RealTimeDelay`, `NoDelay`, `SimulationStats`, `RobotAction`
- [ ] [단순] `ConsoleRenderer` — 이벤트 로그 포맷, 21×11 창 렌더 + T-11
- [ ] [핵심] `RobotStepper` — 계획·시작·진행 3단계, carry, `BuildMoveRequest`/`Apply` 분리 + T-01~T-07, T-09, T-10
- [ ] [핵심] `SimulationLoop.StepOnceAsync / RunAsync` — 종료 조건·종료 코드·틱 초과 계수 + T-08, T-12
- [ ] [단순] `Program.cs` 조립 (맵 로드 → Robot/Task 생성 → Loop) — 로더 예외를 메시지로 출력하고 exit 1
- [ ] [핵심] T-13 통합 실행, `dotnet run` 으로 완료 기준 확인, README 진행 상태 M1 체크

**4. 마무리**
- [ ] [단순] `dotnet format` 통과, 경고 0
- [ ] [단순] README의 M1 체크박스 갱신

---

## M2+ 확장 지점 요약

| 지점 | M1 | 바뀌는 것 | 안 바뀌는 것 |
|---|---|---|---|
| `IMoveArbiter` | `AlwaysGrant` | M2 메모리 점유 판정 → M3 HTTP 클라이언트 → M4 Redis Lua | 시그니처, `RobotStepper`의 호출 위치 |
| `IPathPlanner` | 로컬 A* | M3 FleetController.Api 안으로 이동 (Simulator는 HTTP로 요청) | A* 코드, `PathResult` |
| `SimulationLoop.StepOnceAsync` | 로봇 1대 | 로봇 N대: 요청 수집 → `DecideAsync` 1회 → 적용 | 틱 예산·overrun 계산, `ITickDelay` |
| `RobotStepper` 1단계 | 고정 작업 1건 | 작업 생성기·할당기가 `task`를 공급 | 2·3단계 |
| 콘솔 로그 | `ConsoleRenderer` | M3에서 같은 지점에 이벤트 배치 수집기(`IEventSink`)를 추가 | 로그 포맷 |
| `Robot.PlannedPath` | `PathStep` | 없음 | `PathIndex` 의미 |
| `CellType` | 0~4 | M7 OneWay 5~8 추가, V7 완화 | 나머지 검증 |
