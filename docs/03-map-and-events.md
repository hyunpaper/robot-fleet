# 03. 맵 구조와 이벤트 스키마

## 좌표계

- **1 셀 = 1m × 1m**
- 원점 `(0, 0)` = 좌상단
- X는 오른쪽, Y는 아래쪽으로 증가
- 로봇은 셀 중심에 위치하며, 셀 단위로만 이동

```
     x →
  y  0  1  2  3  4
  ↓ ┌──┬──┬──┬──┬──┐
  0 │ 1│ 1│ 1│ 1│ 1│
    ├──┼──┼──┼──┼──┤
  1 │ 1│ 0│ 0│ 1│ 1│   0 = 설비 (통행 불가)
    ├──┼──┼──┼──┼──┤   1 = 통로 (통행 가능)
  2 │ 1│ 0│ 0│ 1│ 1│
    ├──┼──┼──┼──┼──┤
  3 │ 1│ 1│ 1│ 1│ 1│
    └──┴──┴──┴──┴──┘
```

### 이동 방식

**4방향(상하좌우)만 허용한다.** 대각선 이동을 넣지 않는 이유:

- 대각선으로 지나갈 때 모서리 두 칸을 동시에 점유하는지 판정이 복잡해짐
- 실제 AMR도 좁은 통로에서는 직교 이동이 일반적
- 충돌 판정 로직이 단순해져 교착 처리에 집중할 수 있음

방향은 4가지: `0=North, 1=East, 2=South, 3=West`

회전에도 시간이 든다 (기본 300ms). 직진만 하는 경로가 회전이 많은 경로보다
짧을 수 있으므로, 경로 비용 계산에 회전 비용을 포함한다.

---

## 셀 타입

| 값 | 이름 | 설명 |
|---|---|---|
| `0` | Blocked | 설비·벽. 통행 불가 |
| `1` | Aisle | 일반 통로 |
| `2` | Station | 설비 접점. 적재/하역이 일어나는 칸 |
| `3` | Charge | 충전 스테이션 |
| `4` | Parking | 유휴 로봇 대기 구역 |
| `5` | OneWayN/E/S/W | 일방통행 통로 *(선택 — M7 실험용)* |

`Station`, `Charge`, `Parking`은 통행 가능하되 **목적지 역할**을 겸한다.

---

## 맵 정의 파일

```json
{
  "mapId": "factory-a",
  "version": 1,
  "width": 60,
  "height": 40,
  "cellSizeMeters": 1.0,
  "grid": [
    "111111111111",
    "100000111111",
    "100000111111",
    "111111111111"
  ],
  "stations": [
    { "id": "ST-01", "x": 6,  "y": 1, "type": "pickup",  "equipmentId": "EQ-01" },
    { "id": "ST-02", "x": 6,  "y": 2, "type": "dropoff", "equipmentId": "EQ-02" }
  ],
  "chargers": [
    { "id": "CH-01", "x": 58, "y": 38 }
  ],
  "parkings": [
    { "id": "PK-01", "x": 0, "y": 39 }
  ]
}
```

`grid`를 문자열 배열로 두는 이유 — 텍스트 에디터에서 맵을 눈으로 보며 편집할 수 있다.
JSON 숫자 배열은 사람이 읽을 수 없다.

### 맵 설계 지침

| 항목 | 권장 | 이유 |
|---|---|---|
| 초기 크기 | 50×50 ~ 60×40 | 로봇 수십 대가 돌기에 충분. 더 크면 A* 탐색이 느려짐 |
| 통로 폭 | 주통로 2칸, 지선 1칸 | **1칸 통로가 있어야 교착이 발생한다** |
| 설비 블록 | 3×3 ~ 5×5 덩어리 | 우회 경로가 생기도록 |
| Station 배치 | 설비 가장자리 | 양쪽에서 접근 가능하게 |

> **1칸 폭 통로를 반드시 넣는다.** 모든 통로가 2칸 이상이면 마주보기 충돌이
> 거의 발생하지 않아 교착 처리 로직을 검증할 수 없다.

---

## 도메인 모델

### Robot

```csharp
public sealed class Robot
{
    public string  RobotId   { get; init; }      // "R-001" — $"R-{index:D3}", index 는 1부터
    public string  MapId     { get; init; }
    public Point   Position  { get; set; }       // 현재 셀
    public Heading Heading   { get; set; }       // 0~3
    public RobotStatus Status { get; set; }
    public string? CurrentTaskId { get; set; }
    public int     BatteryPercent { get; set; }
    public IReadOnlyList<PathStep> PlannedPath { get; set; }   // 칸 + 진입 방향 (06 참조)
    public int     PathIndex  { get; set; }      // 경로상 현재 위치
}

public readonly record struct PathStep(Point Cell, Heading Heading);   // Heading = 이 칸에 진입한 방향

public enum RobotStatus : byte
{
    Idle      = 0,   // 작업 없음, 대기 중
    Moving    = 1,   // 이동 중
    Waiting   = 2,   // 충돌 회피로 정지 (손실 시간)
    Loading   = 3,   // 적재 중
    Unloading = 4,   // 하역 중
    Charging  = 5,
    Error     = 6,   // 경로 없음, 교착 해소 실패 등
}
```

`Waiting`을 `Idle`과 구분하는 것이 핵심이다.
- `Idle` = 할 일이 없어서 멈춤 → 작업 할당 문제
- `Waiting` = 할 일이 있는데 길이 막혀 멈춤 → **혼잡 손실**

이 둘을 섞으면 "로봇이 부족한가, 길이 막힌 건가"를 구분할 수 없다.

> **`RobotId` 생성 규칙은 재현성의 일부다.** 10 §1의 `TaskDispatcher`가 동점을 **`robotId` 오름차순**으로 정하므로
> 규칙이 바뀌면 같은 시드로도 할당 결과가 달라진다. 3자리면 999대까지 문자열 정렬이 자연 정렬과 같고,
> 05 실험 1의 최대치(150대)에 충분하다.

### Task

```csharp
public sealed class TransportTask
{
    public string TaskId   { get; init; }        // "T-00042"
    public string MapId    { get; init; }
    public TaskType Type   { get; init; }
    public Point  Origin   { get; init; }        // 적재 지점
    public Point  Destination { get; init; }     // 하역 지점
    public string? OriginStationCode { get; init; }   // "ST-01"
    public string? DestStationCode   { get; init; }
    public string? OriginEquipCode   { get; init; }   // "EQ-01"
    public string? DestEquipCode     { get; init; }
    public int    Priority { get; init; }        // 0=normal, 1=high
    public DateTimeOffset CreatedAt { get; init; }
    public string? AssignedRobotId { get; set; }
    public TaskStatus Status { get; set; }
}
```

> `Origin` · `Destination` 좌표는 **유지**한다. 경로 계획은 좌표로 하고, 설비 식별자는 **분석과 재현**용이다.
> 05 실험 4(맵 3종 비교)는 맵이 바뀌면 좌표가 바뀌므로, **같은 설비 쌍**으로 작업을 재현하려면 좌표만으로는 안 된다.
> 설비 · 접점의 마스터는 01 "기준정보".

```csharp
public enum TaskType : byte
{
    Transport = 0,   // 적재 → 하역
    Charge    = 1,   // 충전소 복귀
    Park      = 2,   // 대기 구역 복귀
}

public enum TaskStatus : byte
{
    Pending   = 0,   // 생성됨, 미할당
    Assigned  = 1,   // 로봇 배정됨
    InProgress = 2,  // 수행 중
    Completed = 3,
    Failed    = 4,   // 경로 없음, 타임아웃
}
```

---

## 이벤트 발행 주체

**모든 이벤트는 Simulator가 발행한다.** FleetController는 판정 결과를 배치 응답에 실어
돌려줄 뿐 IngestApi를 호출하지 않는다.

| 이벤트 | 발생 근거 (누가 사실을 아는가) | 발행자 |
|---|---|---|
| RobotTelemetry | 로봇 위치·상태 — Simulator가 보유 | Simulator |
| TaskCreated | 작업 생성기 — Simulator 내부 | Simulator |
| TaskAssigned | FleetController 할당 API 응답 | Simulator (응답을 이벤트로 변환) |
| TaskStarted / TaskCompleted / TaskFailed | 적재·하역 완료, 도착, 실패 — Simulator가 판정 | Simulator |
| ConflictDetected | 배치 이동 판정 응답의 `granted=false` 항목 | Simulator (응답을 이벤트로 변환) |
| DeadlockDetected | 배치 이동 판정 응답의 `deadlock` 항목 | Simulator (응답을 이벤트로 변환) |
| PathReplanned | 재계획 API 응답 (`oldLength/newLength/computeMs`) | Simulator (응답을 이벤트로 변환) |
| RobotStatusChanged | 로봇 상태 전이 — Simulator가 수행 | Simulator |
| SimulationStarted / SimulationEnded | run 시작 · 종료 — Simulator만 안다 | Simulator |

> **발행자를 하나로 두는 이유** — FleetController는 무상태·다중 pod다. 두 서비스가
> 같은 로봇의 이벤트를 따로 보내면 `RobotStatusChanged`(Simulator)와
> `ConflictDetected`(FleetController)의 선후를 어느 쪽도 보장할 수 없다.
> Simulator 한 곳에서 **틱 안의 발생 순서대로 번호를 매겨** 내보내면 순서 문제가 사라지고,
> FleetController에는 큐 클라이언트도 재시도 로직도 필요 없어진다. 01 다이어그램의 ②가 이 경로다.

---

## 이벤트 스키마

### 공통 봉투

모든 이벤트가 공유하는 필드.

```json
{
  "eventId":    "018f3c2a-7b1e-7c3d-9a2f-0123456789ab/17",   // "{batchId}/{index}"
  "eventType":  "RobotTelemetry",
  "mapId":      "factory-a",
  "runId":      "20261013-143022-r200",   // run 식별자
  "tick":       142057,                   // 시뮬레이터 틱 번호
  "tickMs":     100,                      // 틱 1회의 시뮬레이션 시간 (ms)
  "occurredAt": "2026-04-01T09:14:23.412Z",
  "payload":    { }
}
```

| 필드 | 용도 |
|---|---|
| `eventId` | **멱등 키**. `{batchId}/{index}`. `batchId`는 UUID v7이라 시간순 정렬이 되어 디버깅에 유리 |
| `runId` | Simulator가 run 시작 때 생성해 **그 run의 모든 배치에 싣는다.** 형식은 `{yyyyMMdd}-{HHmmss}-r{robots}` (송신 로그 파일명과 같은 값). `tick`은 run마다 0부터 다시 시작하므로 **run을 가르는 유일한 수단**이다. 배치 봉투 값을 Ingest가 항목마다 복사 |
| `tick` | 시뮬레이터 내부 논리 시각. 배속을 바꿔도 순서가 보존됨 |
| `tickMs` | 틱 1회가 나타내는 시뮬레이션 시간. 워커가 telemetry 1행을 상태별 시간(05 시간 분해)으로 환산할 때 쓴다. 배치 봉투 값을 Ingest가 항목마다 복사 |
| `occurredAt` | 실제 발생 시각. 지연 도착 판정에 사용 |

> `tick`과 `occurredAt`을 둘 다 두는 이유 — 배속 60으로 돌리면 실제 1초에
> 60틱이 지난다. `occurredAt`만으로는 틱 순서를 복원할 수 없다.

> **`runId`를 봉투에 싣는 이유는 `tickMs`와 같다**(09 §2) — 워커가 run을 추론하게 두면 송신 버퍼 60초 때문에
> 전환 경계가 섞이고, 워커 2대가 서로 다른 답을 낸다. 봉투에 싣는 쪽이 유일하게 안전하다.
> run은 "언제 적재됐나"가 아니라 "어느 시뮬레이션이 만들었나"이고, 그건 Simulator만 안다.

### 배치 봉투와 eventId 규칙

Simulator는 틱마다 **그 틱에 발생한 모든 이벤트**를 배치 1건으로 보낸다.

```json
POST /api/v1/events/batch
{
  "batchId":    "018f3c2a-7b1e-7c3d-9a2f-0123456789ab",   // UUID v7, 틱당 1개
  "mapId":      "factory-a",
  "runId":      "20261013-143022-r200",
  "tick":       142057,
  "tickMs":     100,
  "occurredAt": "2026-04-01T09:14:23.412Z",
  "events": [
    { "eventType": "ConflictDetected",  "payload": { "conflictType": "vertex", "x": 18, "y": 6, "robotIds": ["R-002","R-017"], "winnerRobotId": "R-017", "resolution": "wait", "waitMs": 100 } },
    { "eventType": "RobotStatusChanged","payload": { "robotId": "R-002", "from": 1, "to": 2, "reason": "conflict_wait" } },
    { "eventType": "RobotTelemetry",    "payload": { "robotId": "R-001", "x": 3,  "y": 12, "heading": 0, "status": 1, "battery": 91 } },
    { "eventType": "RobotTelemetry",    "payload": { "robotId": "R-002", "x": 18, "y": 7,  "heading": 2, "status": 2, "battery": 64 } }
  ]
}
```

| 규칙 | 내용 |
|---|---|
| `batchId` | Simulator가 틱마다 새로 생성. **재전송 시 같은 값**을 쓴다 (Ingest 멱등 단위) |
| `eventId` | IngestApi가 `"{batchId}/{index}"`로 부여. `index`는 `events` 배열의 0부터 시작하는 위치. 재전송해도 같은 값 → 워커 멱등 단위 |
| 배열 순서 | **틱 안의 발생 순서.** 같은 로봇의 `RobotStatusChanged`는 그 로봇의 `RobotTelemetry`보다 앞에 둔다 (전이 후 상태가 텔레메트리에 반영됨) |
| 상한 | 배치당 1000건. 로봇 200대 + 그 틱의 작업·충돌 이벤트로 충분. 넘치면 같은 틱에 두 배치(`batchId` 별도) |
| 큐 발행 | IngestApi가 항목을 하나씩 큐 메시지로 쪼갠다. 메시지 `MessageId = eventId`, 라우팅 키는 01의 큐별 규칙 |

로봇 200대면 틱당 요청 1건, 초당 10건. 개별 전송이면 초당 2000건.

> **eventId를 UUID가 아닌 `batchId/index`로 두는 이유** — 항목마다 UUID를 만들면
> Simulator가 재전송 전에 그 UUID들을 보관해야 한다. `batchId` 하나만 보관하면
> 항목 id는 위치에서 결정되어 재전송·부분 발행 어느 경우에도 같은 값이 나온다.

---

### 1. RobotTelemetry — 위치·상태 (고빈도)

매 틱, 모든 로봇에 대해 발행. **가장 많은 이벤트.**

```json
{
  "eventType": "RobotTelemetry",
  "payload": {
    "robotId":  "R-012",
    "x":        23,
    "y":        14,
    "heading":  1,
    "status":   1,
    "battery":  87,
    "taskId":   "T-00042",
    "pathIndex": 7,
    "pathLength": 19
  }
}
```

**배치로 전송한다.** 틱마다 로봇 전체가 위의 배치 봉투 `events` 배열에 들어간다.
단독 엔드포인트는 없다.

---

### 2. TaskCreated — 작업 발생

```json
{
  "eventType": "TaskCreated",
  "payload": {
    "taskId":   "T-00042",
    "type":     0,
    "origin":      { "x": 6,  "y": 1 },
    "destination": { "x": 52, "y": 33 },
    "priority": 0
  }
}
```

### 3. TaskAssigned — 로봇 배정

```json
{
  "eventType": "TaskAssigned",
  "payload": {
    "taskId":  "T-00042",
    "robotId": "R-012",
    "plannedPathLength": 19,
    "estimatedDurationMs": 23400
  }
}
```

`plannedPathLength`를 남겨 두면 나중에 **경로 효율**(계획 대비 실제)을 계산할 수 있다.

### 4. TaskStarted / TaskCompleted

```json
{
  "eventType": "TaskCompleted",
  "payload": {
    "taskId":      "T-00042",
    "robotId":     "R-012",
    "actualSteps": 23,          // 실제 이동 칸 수 (재계획으로 늘어남)
    "moveMs":      19200,
    "waitMs":      3100,        // 충돌 회피 대기 누적
    "workMs":      4000,        // 적재+하역
    "replanCount": 2
  }
}
```

**이 이벤트 하나로 효율 지표 대부분이 나온다.**
- 경로 효율 = `plannedPathLength / actualSteps`
- 손실 시간 비율 = `waitMs / (moveMs + waitMs + workMs)`

### 5. TaskFailed

```json
{
  "eventType": "TaskFailed",
  "payload": {
    "taskId":  "T-00042",
    "robotId": "R-012",
    "reason":  "no_path",       // no_path | timeout | deadlock_unresolved | replan_exceeded | battery
    "atX": 23, "atY": 14
  }
}
```

---

### 6. ConflictDetected — 충돌 발생

```json
{
  "eventType": "ConflictDetected",
  "payload": {
    "conflictType": "vertex",        // vertex | edge | following
    "x": 23, "y": 14,
    "robotIds":   ["R-012", "R-034"],
    "winnerRobotId": "R-012",
    "resolution": "wait",            // wait | reroute | yield
    "waitMs": 400
  }
}
```

### 7. DeadlockDetected — 교착

```json
{
  "eventType": "DeadlockDetected",
  "payload": {
    "cycle":      ["R-012", "R-034", "R-007"],   // 순환 대기 체인
    "cells":      [{"x":23,"y":14},{"x":24,"y":14},{"x":24,"y":15}],
    "resolution": "yield",          // yield | failed
    "yieldRobotId": "R-007",
    "yieldToX": 24, "yieldToY": 16,
    "searchedDistance": 5,          // 회피 지점 탐색 거리 (상한에 걸렸으면 = yield_search_max_distance)
    "resolvedMs": 1200
  }
}
```

드물게 발생하지만 **발생률과 해소 시간이 알고리즘 품질의 직접 지표**가 된다.

`resolution: "failed"`는 04의 "양보 지점을 찾지 못하면 `Failed` 처리 — 맵 설계 문제 신호"에 해당한다.
`searchedDistance`가 `yield_search_max_distance`와 같으면 탐색 상한에 걸린 것이다.
M9 1단계가 이 두 값으로 `no_yield_point`를 판정한다(04 "M9 교착 분석 에이전트의 액션 공간").

### 8. PathReplanned — 경로 재계획

```json
{
  "eventType": "PathReplanned",
  "payload": {
    "robotId": "R-012",
    "taskId":  "T-00042",
    "reason":  "blocked",            // blocked | deadlock | map_changed
    "oldLength": 19,
    "newLength": 24,
    "computeMs": 3.4
  }
}
```

### 9. RobotStatusChanged — 상태 전이

텔레메트리에 status가 이미 있지만, **전이 시점만 따로** 발행한다.
텔레메트리는 유실 허용이라 상태 변화를 놓칠 수 있기 때문.

```json
{
  "eventType": "RobotStatusChanged",
  "payload": {
    "robotId": "R-012",
    "from":    1,
    "to":      2,
    "reason":  "conflict_wait"
  }
}
```

### 10. SimulationStarted · SimulationEnded — run 시작 · 종료

라우팅 키는 **`mapId`**(로봇에 속하지 않는다).

```json
{
  "eventType": "SimulationStarted",
  "payload": {
    "robots":   200,
    "speed":    1.0,
    "tickMs":   100,
    "taskSeed": 20261013,
    "environment": "local",           // local | aws
    "lineCode": "L-A",
    "mapId":    "factory-a",
    "mapVersion": 1,
    "mapHash":  "sha256:9f1c…",
    "taskPatternCode": "P-A-standard",
    "params": { "congestion_alpha": 0, "turn_cost": 0.5 }
  }
}
```

```json
{
  "eventType": "SimulationEnded",
  "payload": {
    "ticks": 72000, "tasksCreated": 4180, "tasksCompleted": 4102, "tasksFailed": 11,
    "overruns": 3, "stallMs": 0, "reason": "max_ticks"
  }
}
```

| 규칙 | 내용 |
|---|---|
| 발행 위치 | `SimulationStarted`는 **첫 배치의 맨 앞**, `SimulationEnded`는 **마지막 배치의 맨 뒤** |
| 큐 범주 | `task`와 같은 **quorum 큐**(유실 불가). 실험 조건이 사라지면 그 run 전체가 무의미해진다 |
| `reason` | `max_ticks` / `stopped` / `faulted` |
| `environment` | `local`(집 PC VM) / `aws`(M7). 하드웨어가 바뀌면 다른 조건이다(05 "동일 조건에서 변수 하나만") |
| `params` | 04 "설정값" 13종의 스냅샷. 05가 요구하는 실험 조건 기록이 자동으로 남고, M9의 "과거 동일 유형 조치와 결과"도 이 값으로 조인한다 |
| `mapHash` | Simulator가 읽은 맵 파일의 해시. DB `maps`의 해당 버전 해시와 다르면 경고 — **맵 파일을 고치고 `version`을 안 올리면 과거 run의 해석이 조용히 틀어진다** |
| 워커 처리 | `simulations` 테이블 upsert (01) |

---

## 이벤트 → 큐 매핑

| 이벤트 | 큐 | persistent | 유실 허용 |
|---|---|---|---|
| RobotTelemetry | `fleet.telemetry` | no | 예 |
| TaskCreated / Assigned / Started / Completed / Failed | `fleet.task` | **yes** | 아니오 |
| ConflictDetected | `fleet.conflict` | yes | 아니오 |
| DeadlockDetected | `fleet.conflict` | yes | 아니오 |
| PathReplanned | `fleet.conflict` | no | 예 |
| RobotStatusChanged | `fleet.task` | yes | 아니오 |
| SimulationStarted / SimulationEnded | `fleet.task` | yes | 아니오 |

---

## 상태 전이 규칙

유효하지 않은 전이는 **거부하고 별도 큐로 보낸다.** 아래 표가 전체이며, 표에 없는 전이는 전부 금지다.

| from \ to | Idle | Moving | Waiting | Loading | Unloading | Charging | Error |
|---|---|---|---|---|---|---|---|
| **Idle** | — | `task_assigned` | ✗ | ✗ | ✗ | `charge_at_station` | `fault` |
| **Moving** | `arrived_park` `arrived_charge_denied` | — | `conflict_wait` | `arrived_origin` | `arrived_destination` | `arrived_charger` | `no_path` `replan_exceeded` `fault` |
| **Waiting** | ✗ | `path_cleared` `replanned` `yield` | — | ✗ | ✗ | ✗ | `deadlock_unresolved` `fault` |
| **Loading** | ✗ | `load_done` | ✗ | — | ✗ | ✗ | `fault` |
| **Unloading** | `unload_done` | ✗ | ✗ | ✗ | — | ✗ | `fault` |
| **Charging** | `charge_done` | ✗ | ✗ | ✗ | ✗ | — | `fault` |
| **Error** | `recovered` | ✗ | ✗ | ✗ | ✗ | ✗ | — |

셀의 값은 `RobotStatusChanged.reason`이다. 빈틈을 메운 결정과 근거는 다음과 같다.

| 결정 | 근거 |
|---|---|
| **Park 작업 완료 = `Moving → Idle`** (`arrived_park`) | 대기 구역 도착은 하역이 없다. `Unloading`을 거치게 하면 Work 시간이 왜곡된다 |
| **Charge 작업 완료 = `Moving → Charging`** (`arrived_charger`), 충전 끝 = `Charging → Idle` | 충전은 도착 즉시 시작. 충전 시간은 Work가 아니라 별도 `Charging`으로 집계 (05 시간 분해의 네 범주 어디에도 넣지 않고 분모에서 제외) |
| `Moving → Idle` (`arrived_charge_denied`) | 충전소에 도착했는데 다른 로봇이 쓰고 있으면 옆 칸에서 Idle. 다음 할당 주기에 Charge 작업을 다시 받는다 |
| **`Idle → Charging`** (`charge_at_station`) | 충전소 칸 위에서 Idle이 된 로봇(충전 완료 직후 할 일이 없는 경우)은 이동 없이 다시 충전 가능. `Charging → Idle → Charging` 반복을 허용 |
| **`Waiting → Charging` 금지** | 길이 막힌 로봇은 제자리다. 충전은 충전소 칸에서만 일어나므로 `Waiting → Moving → Charging`을 거쳐야 한다. 배터리가 바닥나면 `Waiting → Error(fault)` |
| 배터리 부족 → Charge 작업 **할당** | "any → Charging"은 폐기. 배터리 부족은 상태 전이가 아니라 **Charge 작업을 최우선으로 할당**하는 일이고, 상태는 작업 흐름을 따라 `Idle → Moving → Charging`으로 간다. 수행 중 작업이 있으면 그 작업을 마친 뒤 할당 |
| **`Error → Idle`만 허용** (`recovered`) | 복구는 운영자 또는 자동 복구기(M8)가 로봇을 현재 칸에서 Idle로 되돌리는 것. 진행 중이던 작업은 이미 `Failed`이므로 새로 할당받는다. `Error → Moving` 직행 금지 |
| `Waiting → Moving` 사유 셋 | `path_cleared`(앞이 비어 원래 경로로), `replanned`(우회), `yield`(교착 양보로 회피 지점 이동 시작). 05 재계획률·교착 지표가 이 사유를 센다 |
| `Loading → Moving`만 허용 | `Loading → Unloading` 금지 (이동 없이 하역 불가). 적재 지점과 하역 지점이 인접해도 한 칸은 이동한다 |

**금지 전이 예시**
- `Idle → Loading` — 이동 없이 적재할 수 없음
- `Loading → Unloading` — 이동 없이 하역할 수 없음
- `Charging → Moving` — 충전 완료 후 Idle을 거쳐야 함
- `Waiting → Charging` — 충전소 칸까지 이동해야 함
- `Error → Moving` — Idle을 거쳐 새 작업을 받아야 함

워커가 이를 검증한다. 위반하면 이벤트 순서가 뒤바뀐 것이므로
`fleet.invalid` 큐로 보내고 메트릭을 올린다. **파티셔닝이 제대로 동작하는지 확인하는 장치**이기도 하다.

> **M1 범위** — `Idle → Moving(task_assigned)`, `Moving → Idle(arrived_park)` 두 전이만 쓴다.
> 로봇 1대가 목적지에 도착하면 Idle로 돌아간다. 나머지는 M2부터.

---

## 샘플 데이터 생성 규칙

### 작업 발생

포아송 분포로 생성한다. 평균 발생률 `λ`를 설정으로 조절.

```
λ = 0.5 건/초  →  로봇 10대 수준
λ = 5   건/초  →  로봇 100대 수준
```

출발지·목적지는 작업 패턴(01 기준정보 `task_pattern_flows`)의 `weight`에 따라 **설비 쌍**을 추첨한다.
출발지와 목적지가 같은 흐름은 DB 제약(`ck_flow_distinct`)이 막는다. 시드를 고정하면 같은 작업열이 재현된다.

### 로봇 초기 배치

Parking 구역에 배치한다. 무작위로 흩뿌리면 시작 직후 충돌이 몰려
초기 구간 통계가 왜곡된다.

### 배터리

이동 1칸당 0.05%, 작업 1회당 0.2% 소모. 20% 이하면 충전 작업을 우선 할당.
→ 충전소가 부족하면 그 자체로 병목이 되는 걸 관찰할 수 있다.
