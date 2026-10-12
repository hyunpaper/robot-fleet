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
    public string  RobotId   { get; init; }      // "R-001"
    public string  MapId     { get; init; }
    public Point   Position  { get; set; }       // 현재 셀
    public Heading Heading   { get; set; }       // 0~3
    public RobotStatus Status { get; set; }
    public string? CurrentTaskId { get; set; }
    public int     BatteryPercent { get; set; }
    public IReadOnlyList<Point> PlannedPath { get; set; }
    public int     PathIndex  { get; set; }      // 경로상 현재 위치
}

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

### Task

```csharp
public sealed class TransportTask
{
    public string TaskId   { get; init; }        // "T-00042"
    public string MapId    { get; init; }
    public TaskType Type   { get; init; }
    public Point  Origin   { get; init; }        // 적재 지점
    public Point  Destination { get; init; }     // 하역 지점
    public int    Priority { get; init; }        // 0=normal, 1=high
    public DateTimeOffset CreatedAt { get; init; }
    public string? AssignedRobotId { get; set; }
    public TaskStatus Status { get; set; }
}

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

## 이벤트 스키마

### 공통 봉투

모든 이벤트가 공유하는 필드.

```json
{
  "eventId":    "018f3c2a-...",          // UUID v7 — 시간순 정렬 가능
  "eventType":  "RobotTelemetry",
  "mapId":      "factory-a",
  "tick":       142057,                   // 시뮬레이터 틱 번호
  "occurredAt": "2026-04-01T09:14:23.412Z",
  "payload":    { }
}
```

| 필드 | 용도 |
|---|---|
| `eventId` | **멱등 키**. UUID v7을 쓰면 시간순 정렬이 되어 디버깅에 유리 |
| `tick` | 시뮬레이터 내부 논리 시각. 배속을 바꿔도 순서가 보존됨 |
| `occurredAt` | 실제 발생 시각. 지연 도착 판정에 사용 |

> `tick`과 `occurredAt`을 둘 다 두는 이유 — 배속 60으로 돌리면 실제 1초에
> 60틱이 지난다. `occurredAt`만으로는 틱 순서를 복원할 수 없다.

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

**배치로 전송한다.** 틱마다 로봇 전체를 하나의 배열로 묶는다.

```json
POST /api/v1/events/batch
{
  "mapId": "factory-a",
  "tick": 142057,
  "occurredAt": "2026-04-01T09:14:23.412Z",
  "telemetry": [
    { "robotId": "R-001", "x": 3,  "y": 12, "heading": 0, "status": 1, "battery": 91 },
    { "robotId": "R-002", "x": 18, "y": 7,  "heading": 2, "status": 2, "battery": 64 }
  ]
}
```

로봇 200대면 틱당 요청 1건, 초당 10건. 개별 전송이면 초당 2000건.

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
    "reason":  "no_path",       // no_path | timeout | deadlock_unresolved | battery
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
    "resolution": "yield",
    "yieldRobotId": "R-007",
    "yieldToX": 24, "yieldToY": 16,
    "resolvedMs": 1200
  }
}
```

드물게 발생하지만 **발생률과 해소 시간이 알고리즘 품질의 직접 지표**가 된다.

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

---

## 상태 전이 규칙

유효하지 않은 전이는 **거부하고 별도 큐로 보낸다.**

```
Idle ──────► Moving      (작업 할당됨)
Moving ────► Waiting     (충돌 회피)
Waiting ───► Moving      (길 열림)
Moving ────► Loading     (적재 지점 도착)
Loading ───► Moving      (적재 완료)
Moving ────► Unloading   (하역 지점 도착)
Unloading ─► Idle        (작업 완료)
any ───────► Error       (실패)
any ───────► Charging    (배터리 부족)
Charging ──► Idle        (충전 완료)
```

**금지 전이 예시**
- `Idle → Loading` — 이동 없이 적재할 수 없음
- `Loading → Unloading` — 이동 없이 하역할 수 없음
- `Charging → Moving` — 충전 완료 후 Idle을 거쳐야 함

워커가 이를 검증한다. 위반하면 이벤트 순서가 뒤바뀐 것이므로
`fleet.invalid` 큐로 보내고 메트릭을 올린다. **파티셔닝이 제대로 동작하는지 확인하는 장치**이기도 하다.

---

## 샘플 데이터 생성 규칙

### 작업 발생

포아송 분포로 생성한다. 평균 발생률 `λ`를 설정으로 조절.

```
λ = 0.5 건/초  →  로봇 10대 수준
λ = 5   건/초  →  로봇 100대 수준
```

출발지·목적지는 Station 목록에서 무작위 선택하되, **같은 지점이면 재추첨**한다.

### 로봇 초기 배치

Parking 구역에 배치한다. 무작위로 흩뿌리면 시작 직후 충돌이 몰려
초기 구간 통계가 왜곡된다.

### 배터리

이동 1칸당 0.05%, 작업 1회당 0.2% 소모. 20% 이하면 충전 작업을 우선 할당.
→ 충전소가 부족하면 그 자체로 병목이 되는 걸 관찰할 수 있다.
