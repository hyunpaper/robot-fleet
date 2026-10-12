# 01. 아키텍처

## 전체 구성

```
┌──────────────┐
│  Simulator   │  가상 공장 · 로봇 물리 이동 · 작업 발생
│  (Worker)    │
└──────┬───────┘
       │ ① 이동 요청 (다음 칸 진입 가능?)
       ↓
┌──────────────┐        ┌─────────┐
│ FleetControl │◄──────►│  Redis  │  셀 점유 락 · 로봇 상태 · 예약 테이블
│  (API×N)     │        └─────────┘
└──────┬───────┘
       │ ② 이벤트 발행
       ↓
┌──────────────┐
│  IngestApi   │  수신 · 멱등 검사 · 큐 발행
│  (API×3)     │
└──────┬───────┘
       ↓
┌──────────────┐
│  RabbitMQ    │  telemetry / task / conflict 큐 분리
└──────┬───────┘
       ↓
┌──────────────┐        ┌────────────┐
│ EventWorker  │───────►│ PostgreSQL │  이력 · 집계
│  (Worker×M)  │        └────────────┘
└──────────────┘               ↑
                               │
                        ┌──────────────┐
                        │   QueryApi   │ → 관제 화면 · Grafana
                        └──────────────┘
```

---

## 서비스 분리

### 왜 나누는가

| 서비스 | 특성 | 확장 기준 |
|---|---|---|
| Simulator | CPU 바운드, 상태 보유 | 맵 단위로 1개 (분할 불가) |
| FleetController | 짧은 판단 × 고빈도 호출 | 요청 수 |
| IngestApi | I/O 바운드, 무상태 | 유입량 |
| EventWorker | 쓰기 집중 | 큐 적체량 |
| QueryApi | 읽기 집중 | 조회 수 |

**붙여 놓으면 안 되는 이유**
- 시뮬레이터가 한 틱을 계산하는 동안 이벤트 수신이 멈추면 안 됨
- 이벤트 적재가 느려져도 로봇은 계속 움직여야 함
- 관제 판단(수 ms)과 DB 쓰기(수십 ms)는 응답 시간 요구가 다름

---

### Simulator

가상 공장의 물리 세계를 담당한다.

- 틱 루프 (기본 100ms, 배속 조절 가능)
- 로봇 위치·방향·배터리 갱신
- 작업 발생 (포아송 분포 기반 랜덤 생성)
- FleetController에 다음 이동 가능 여부 질의
- 매 틱 텔레메트리 이벤트 발행

**상태를 가진 유일한 서비스.** 맵 하나당 인스턴스 하나이며 수평 확장하지 않는다.
여러 맵을 돌리려면 맵별로 인스턴스를 띄운다.

```
while (running)
{
    var sw = Stopwatch.StartNew();

    foreach (var robot in robots)
        robot.Step(tickMs);          // 이동 · 작업 진행

    taskGenerator.Tick(tickMs);      // 신규 작업 발생
    PublishTelemetry();              // 이벤트 발행

    var elapsed = sw.ElapsedMilliseconds;
    var wait = (tickMs / speedFactor) - elapsed;
    if (wait > 0)
        await Task.Delay((int)wait);
    else
        metrics.TickOverrun.Inc();   // 틱 초과 — 계산이 못 따라감
}
```

> **틱 초과(overrun)를 반드시 메트릭으로 노출한다.** 로봇 수를 늘리다 보면
> 한 틱 계산이 100ms를 넘는 순간이 오고, 그 지점이 시뮬레이터의 한계점이다.

---

### FleetController

로봇의 이동을 승인하거나 거부한다. **시스템에서 가장 중요한 판단 지점.**

책임
- 경로 계획 (A*)
- 다음 칸 진입 승인 — Redis 분산 락
- 교착 탐지와 해소
- 작업 할당 (유휴 로봇 ↔ 대기 작업 매칭)

무상태로 설계한다. 모든 상태는 Redis에 둔다. 그래야 pod를 여러 개 띄울 수 있다.

```
POST /api/v1/move-request
{
  "robotId": "R-012",
  "from": { "x": 10, "y": 5 },
  "to":   { "x": 11, "y": 5 },
  "tick": 142057
}

200 { "granted": true,  "lockToken": "...", "expiresAt": "..." }
200 { "granted": false, "reason": "occupied", "suggestedAction": "wait" }
200 { "granted": false, "reason": "deadlock", "suggestedAction": "yield", "yieldTo": {"x":10,"y":6} }
```

---

### IngestApi

이벤트를 받아 큐에 넣는 것만 한다. **DB에 직접 쓰지 않는다.**

- 스키마 검증
- 멱등 키 확인 (Redis)
- 큐 발행
- 즉시 202 응답

```
POST /api/v1/events          단건
POST /api/v1/events/batch    배치 (최대 500건)
```

배치 엔드포인트를 반드시 둔다. 로봇 200대 × 10Hz면 초당 2000건인데
건당 HTTP 요청은 낭비가 크다. 시뮬레이터가 틱 단위로 묶어 보낸다.

---

### EventWorker

큐를 소비해 적재하고 집계한다.

- 이벤트 종류별 핸들러
- 배치 삽입 (개별 INSERT 금지)
- 증분 집계 갱신
- 실패 시 재시도 → 초과하면 DLQ

---

### QueryApi

읽기 전용. 관제 화면과 대시보드에 데이터를 공급한다.

```
GET /api/v1/robots                 현재 전체 로봇 상태
GET /api/v1/robots/{id}/trace      경로 이력
GET /api/v1/metrics/utilization    가동률
GET /api/v1/metrics/heatmap        혼잡 히트맵
GET /api/v1/map                    맵 정의
WS  /ws/live                       실시간 로봇 위치 (관제 화면용)
```

---

## 데이터 흐름

### 이동 판단 (동기 · 저지연)

```
Simulator → FleetController → Redis
          ←                 ←
```

**반드시 동기로 처리한다.** 로봇이 움직여도 되는지는 즉답이 필요하다.
큐를 거치면 판단이 늦어져 충돌이 발생한다.

목표 지연: p99 10ms 이내

### 이벤트 적재 (비동기)

```
Simulator → IngestApi → RabbitMQ → EventWorker → PostgreSQL
```

**비동기로 처리한다.** 적재가 느려도 로봇 운행에 영향을 주지 않아야 한다.

허용 지연: 수 초 (실시간 관제 화면은 WebSocket으로 별도 공급)

---

## RabbitMQ 설계

### 큐 분리

| 큐 | 이벤트 | 빈도 | 우선순위 |
|---|---|---|---|
| `fleet.telemetry` | 위치·상태 | 초당 수백~수천 | 낮음 (유실 허용) |
| `fleet.task` | 작업 할당·시작·완료 | 로봇당 수십 초 | 높음 (유실 불가) |
| `fleet.conflict` | 충돌 대기, 교착, 재계획 | 수시 | 중간 |

**분리하는 이유** — 텔레메트리가 초당 수천 건 쏟아질 때 작업 완료 이벤트가
그 뒤에 줄을 서면 실적 집계가 지연된다. 중요도가 다른 메시지를 같은 큐에 넣지 않는다.

### 순서 보장

같은 로봇의 이벤트는 **순서대로 처리되어야 한다.**
`TaskCompleted`가 `TaskStarted`보다 먼저 처리되면 상태가 깨진다.

**Consistent Hash Exchange**를 사용해 `robotId`로 파티셔닝한다.

```
fleet.telemetry (x-consistent-hash)
  ├── fleet.telemetry.p0  ← Worker A
  ├── fleet.telemetry.p1  ← Worker B
  ├── ...
  └── fleet.telemetry.p7  ← Worker H
```

- 같은 `robotId` → 항상 같은 파티션 → 한 워커가 순서대로 처리
- 파티션 수만큼 워커를 늘릴 수 있음 (그 이상은 늘려도 소용없음)
- 파티션 수는 예상 최대 워커 수보다 넉넉하게 (예: 16)

> **확장성과 순서 보장의 trade-off.** 파티션 수가 동시 처리 가능한 워커 수의
> 상한이 된다. 무한정 늘릴 수 없다는 것이 이 설계의 제약이며,
> 순서가 중요한 시스템에서 피할 수 없는 선택이다.

### 메시지 지속성

| 큐 | durable | 메시지 persistent | 비고 |
|---|---|---|---|
| telemetry | yes | **no** | 유실돼도 다음 틱에 갱신됨. 성능 우선 |
| task | yes | **yes** | 실적 데이터. 유실 불가 |
| conflict | yes | yes | 분석 자료 |

M5 이후 `task` 큐를 **quorum queue**로 전환한다.
→ 단일 노드 → 복제 3노드로 바꾸는 과정 자체를 기록으로 남긴다.

---

## Redis 설계

### 키 구조

```
# 셀 점유 락 (TTL 필수)
cell:lock:{mapId}:{x}:{y}        → robotId          TTL 2s

# 로봇 현재 상태
robot:state:{robotId}            → Hash {x, y, heading, status, taskId, battery}

# 시공간 예약 (예약 방식 사용 시)
reserve:{mapId}:{x}:{y}:{tick}   → robotId          TTL 5s

# 멱등 키
idem:{eventId}                   → 1                TTL 10m

# 작업 대기열
task:pending:{mapId}             → List<taskId>

# 집계 캐시
metrics:robot:{robotId}:today    → Hash {moveMs, waitMs, idleMs, taskCount}
metrics:heatmap:{mapId}          → Hash {"x:y" → count}
```

### 락에 TTL을 반드시 거는 이유

로봇이 셀을 잠근 채 프로세스가 죽으면 **그 칸은 영원히 통행 불가**가 된다.
맵 중앙 통로에서 이런 일이 생기면 공장 전체가 멈춘다.

- TTL 2초 (틱 20회분)
- 정상 이동 완료 시 즉시 해제
- 갱신이 필요하면 연장 (lock extension)

### 해제 시 토큰 검증

```lua
-- 내가 건 락만 해제한다
if redis.call("GET", KEYS[1]) == ARGV[1] then
    return redis.call("DEL", KEYS[1])
else
    return 0
end
```

TTL 만료 후 다른 로봇이 그 칸을 잠갔는데, 늦게 도착한 해제 요청이
남의 락을 풀어 버리는 사고를 막는다.

---

## PostgreSQL 설계

### 테이블

```sql
-- 로봇 위치 이력 (대용량 · 파티셔닝)
CREATE TABLE robot_telemetry (
    id          BIGSERIAL,
    map_id      TEXT        NOT NULL,
    robot_id    TEXT        NOT NULL,
    tick        BIGINT      NOT NULL,
    x           SMALLINT    NOT NULL,
    y           SMALLINT    NOT NULL,
    heading     SMALLINT    NOT NULL,
    status      SMALLINT    NOT NULL,
    battery     SMALLINT,
    occurred_at TIMESTAMPTZ NOT NULL
) PARTITION BY RANGE (occurred_at);

-- 일 단위 파티션. 오래된 파티션은 통째로 DROP
CREATE TABLE robot_telemetry_20260401
    PARTITION OF robot_telemetry
    FOR VALUES FROM ('2026-04-01') TO ('2026-04-02');

-- 작업 이력
CREATE TABLE task_history (
    task_id      TEXT PRIMARY KEY,
    map_id       TEXT NOT NULL,
    robot_id     TEXT,
    task_type    SMALLINT NOT NULL,
    origin_x     SMALLINT, origin_y SMALLINT,
    dest_x       SMALLINT, dest_y SMALLINT,
    created_at   TIMESTAMPTZ NOT NULL,
    assigned_at  TIMESTAMPTZ,
    started_at   TIMESTAMPTZ,
    completed_at TIMESTAMPTZ,
    status       SMALLINT NOT NULL,
    path_length  INT,          -- 계획 경로 길이
    actual_steps INT,          -- 실제 이동 칸 수
    wait_ms      INT           -- 충돌 대기 누적
);

-- 충돌 이력
CREATE TABLE conflict_log (
    id            BIGSERIAL PRIMARY KEY,
    map_id        TEXT NOT NULL,
    tick          BIGINT NOT NULL,
    conflict_type SMALLINT NOT NULL,   -- vertex / edge / deadlock
    x             SMALLINT, y SMALLINT,
    robot_ids     TEXT[] NOT NULL,
    resolution    SMALLINT,            -- wait / reroute / yield
    wait_ms       INT,
    occurred_at   TIMESTAMPTZ NOT NULL
);

-- 집계 (분 단위 롤업)
CREATE TABLE metrics_minute (
    map_id      TEXT NOT NULL,
    robot_id    TEXT NOT NULL,
    minute      TIMESTAMPTZ NOT NULL,
    move_ms     INT NOT NULL DEFAULT 0,
    work_ms     INT NOT NULL DEFAULT 0,
    wait_ms     INT NOT NULL DEFAULT 0,
    idle_ms     INT NOT NULL DEFAULT 0,
    task_count  INT NOT NULL DEFAULT 0,
    PRIMARY KEY (map_id, robot_id, minute)
);
```

### 텔레메트리 적재 전략

초당 수천 건을 개별 INSERT하면 DB가 먼저 죽는다.

- `COPY` 또는 다중 행 INSERT로 **배치 삽입** (100~500건 묶음)
- 인덱스 최소화 — 조회는 주로 집계 테이블에서
- 일 단위 파티션, 보존 기간 지나면 `DROP PARTITION` (DELETE 금지)

---

## 멱등 처리

모든 이벤트는 `eventId`(UUID)를 갖는다.

```
1. IngestApi: SET idem:{eventId} 1 NX EX 600
2. 결과가 false(이미 존재)면 → 큐에 넣지 않고 202 반환
3. 결과가 true면 → 큐 발행
```

**왜 Ingest 단계에서 거르는가** — 워커에서 거르면 중복 메시지가 큐를 거쳐
워커까지 가서 버려진다. 앞단에서 막는 게 자원을 아낀다.

**TTL 10분의 근거** — 재전송은 네트워크 오류 복구 과정에서 수 초~수 분 내에 발생한다.
그보다 오래된 중복은 실질적으로 없다. TTL이 길면 Redis 메모리가 계속 증가한다.

워커 측에도 2차 방어를 둔다. DB 유니크 제약(`task_id`)으로 최종 보장.

---

## 서비스 간 통신 요약

| 구간 | 방식 | 이유 |
|---|---|---|
| Simulator → FleetController | HTTP 동기 | 즉답 필요 (이동 승인) |
| FleetController ↔ Redis | TCP | 저지연 상태 조회 |
| Simulator → IngestApi | HTTP 배치 | 틱 단위로 묶어 전송 |
| IngestApi → RabbitMQ | AMQP | 비동기 분리 |
| RabbitMQ → EventWorker | AMQP | 파티션별 순서 보장 |
| QueryApi → 관제 화면 | WebSocket | 실시간 위치 푸시 |
