# 01. 아키텍처

## 전체 구성

```
┌──────────────┐
│  Simulator   │  가상 공장 · 로봇 물리 이동 · 작업 발생 · 모든 이벤트 발행
│  (Worker)    │
└──┬───────┬───┘
   │       │ ① 틱 단위 배치 이동 요청 (동기)
   │       ↓
   │   ┌──────────────┐        ┌─────────┐
   │   │ FleetControl │◄──────►│  Redis  │  셀 점유 락 · 로봇 상태 · 대기 그래프
   │   │  (API×2)     │        └─────────┘
   │   └──────────────┘
   │       판정 결과(충돌·교착·재계획 포함)를 응답으로 돌려줌
   │ ② 틱 단위 이벤트 배치 (비동기)
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
- FleetController에 **틱당 1회, 이동하려는 로봇 전체를 묶어** 진입 가능 여부 질의
- 매 틱 발생한 **모든 이벤트**(텔레메트리·작업·충돌·교착)를 하나의 배치로 발행

**상태를 가진 유일한 서비스.** 맵 하나당 인스턴스 하나이며 수평 확장하지 않는다.
여러 맵을 돌리려면 맵별로 인스턴스를 띄운다.

```
while (running)
{
    var sw = Stopwatch.StartNew();
    tick++;

    var requests = robots.Where(r => r.WantsToMove).Select(r => r.NextMoveRequest(tick));
    var decisions = await fleet.DecideBatchAsync(mapId, tick, requests);   // HTTP 1회

    foreach (var robot in robots)
        robot.Step(tickMs, decisions[robot.RobotId]);   // 이동 · 회전 · 작업 진행

    taskGenerator.Tick(tickMs);      // 신규 작업 발생
    PublishEventBatch(tick);         // 이 틱의 모든 이벤트를 1건의 배치로

    var elapsed = sw.ElapsedMilliseconds;
    var wait = (tickMs / speedFactor) - elapsed;
    if (wait > 0)
        await Task.Delay((int)wait);
    else
        metrics.TickOverrun.Inc();   // 틱 초과 — 계산이 못 따라감
}
```

> **이동 요청을 틱당 1회로 묶는 이유** — 로봇 200대가 각각 동기 HTTP를 보내면
> 요청당 2~5ms만 잡아도 틱 하나에 400ms~1s가 걸려 100ms 틱을 절대 못 맞춘다.
> 틱당 1건의 배치 호출이면 HTTP 왕복 1회 + 판정 수 ms로 예산 안에 든다.
> 이동 판정 p99 10ms 목표는 **배치 1건 기준**이다.

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

인스턴스는 **최대 2대**다. 한 틱의 배치는 한 인스턴스가 통째로 판정하므로 3대째부터는 판정 처리량이
늘지 않는다. 2대는 가용성과 경로 계산 분담용이며, 장애 시 같은 틱 배치를 다른 인스턴스로 재전송한다(멱등). → 07

**이벤트는 발행하지 않는다.** 판정 결과(충돌·교착·재계획 정보)를 응답에 실어 돌려주고,
이벤트로 바꿔 발행하는 일은 Simulator가 한다. (→ 03 "이벤트 발행 주체")

#### 틱 단위 배치 이동 판정 API

```
POST /api/v1/move-requests/batch
{
  "mapId": "factory-a",
  "tick": 142057,
  "epoch": "e-7f3a",                       // Redis 상태 세대 (→ "Redis 상태 소실 복구")
  "prev": { "tick": 142056, "digest": "3be1…" },   // 마지막으로 적용한 판정 (→ 08 해시 체인)
  "releases": [ { "robotId": "R-020", "x": 7, "y": 3 } ],   // 직전 틱 이후 이동 완료로 비운 칸
  "requests": [
    {
      "robotId": "R-012",
      "from": { "x": 10, "y": 5 },
      "to":   { "x": 11, "y": 5 },
      "heading": 1,
      "waitMs": 400,                       // 우선순위 계산 입력 (04 참조)
      "remainingSteps": 12,
      "taskPriority": 0
    }
  ]
}

200
{
  "tick": 142057,
  "digest": "a07c…",                       // 다음 요청의 prev.digest
  "decisions": [
    { "robotId": "R-012", "granted": true,  "lockToken": "R-012:142057" },
    { "robotId": "R-034", "granted": false, "reason": "occupied", "blockedBy": "R-012",
      "suggestedAction": "wait" },
    { "robotId": "R-007", "granted": false, "reason": "deadlock",  "suggestedAction": "yield",
      "yieldTo": { "x": 24, "y": 16 },
      "deadlock": { "cycle": ["R-007","R-012","R-034"], "cells": [ ... ] } }
  ]
}

409 { "error": "state_lost", "cause": "missing" }   // missing · epoch · chain · conflict · from_mismatch → 복구 절차 진입
409 { "error": "batch_mismatch" }                   // 같은 tick에 다른 본문 → Simulator는 state_lost와 같이 처리
503 { "error": "unavailable" }                      // Redis 오류 → Simulator가 다른 인스턴스로 재전송
```

요청 1건에 그 틱에 움직이려는 로봇 전부가 들어간다(상한 1000). 이동 요청이 없어도 매 틱 보낸다(해제 전달 · 체인 연속).
`ReleaseAsync`는 즉시 호출하지 않고 다음 배치의 `releases`로 모은다. 재전송 정책과 응답 처리는 08 §4 · §5.
단건 `POST /api/v1/move-request`는 디버깅용으로만 남기고 Simulator는 쓰지 않는다.

#### 배치 내 판정 순서

같은 배치 안의 두 로봇이 같은 칸(정점 충돌)이나 서로의 칸(교차 충돌)을 노릴 수 있다.
Redis에 가서 경쟁시키지 않고 **메모리에서 먼저 정렬·판정한 뒤 Redis에는 결과만 반영**한다.

| 단계 | 내용 |
|---|---|
| 0 | 읽기 Lua 1회로 체인 검사 + 요청 로봇의 칸 · 대기 그래프 · 교착 해소 상태를 읽는다. 재요청이면 기록을 반환하고 끝 (08 §2) |
| 1 | 요청을 04의 우선순위 점수로 **내림차순 정렬**. 동점이면 `robotId` 오름차순 (결정적) |
| 2 | 정렬 순서대로 순차 판정. 배치 내 선행 로봇이 이미 차지한 `to` 칸은 점유로 취급 |
| 3 | 교차 판정: `A.to == B.from && B.to == A.from`이면 후순위 로봇 거부 (`reason: swap`) |
| 4 | 읽어 온 칸 점유와 대조. 남이 점유한 칸은 `occupied` |
| 5 | 거부된 로봇의 대기 관계를 갱신하고 교착 탐지 실행 (04) |
| 6 | 락 · 해제 · 대기 그래프 · 교착 해소 · 판정 기록을 커밋 Lua 1회로 원자 반영, `WAIT 1` 파이프라인 (08 §4) |

> 2칸 선점(04)이라 맞바꿈 쌍은 선순위 로봇도 상대의 현재 칸에 막혀 `occupied`가 된다.
> 후순위가 `swap`으로 응답되도록 구현은 로봇마다 3단계(교차)를 2단계(점유)보다 먼저 검사한다.
> 거부 응답의 `blockedBy`는 막고 있는 로봇이며, 5단계 대기 관계의 입력이다.

> **왜 메모리에서 먼저 판정하는가** — 배치 안의 충돌은 전부 같은 pod가 보고 있으므로
> Redis 왕복 없이 결정할 수 있다. Redis 왕복은 배치당 2회(읽기 · 커밋)로 고정되어
> 로봇 수가 늘어도 판정 지연이 선형으로 늘지 않는다. 교착 해소까지 판정 기록에 넣어
> 락과 같은 원자 단위로 커밋하려면 읽기와 커밋을 나눠야 한다(08 C4).

한 틱의 배치는 하나의 pod가 통째로 처리한다. Simulator가 틱마다 다른 pod로 보내도
상태는 전부 Redis에 있으므로 문제없다. 응답 유실로 같은 `(mapId, tick)`이 재전송되면
기록된 판정을 그대로 돌려준다(08 §4 "재전송 멱등 Lua 규약").

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
배치는 `batchId` 하나로 멱등 검사하고, 큐에는 **항목 단위**로 쪼개 발행한다
(순서 보장이 `robotId`/`taskId` 라우팅 키를 요구하기 때문). 항목의 `eventId` 규칙은 03 참조.

---

### EventWorker

큐를 소비해 적재하고 집계한다.

- 이벤트 종류별 핸들러
- 배치 삽입 (개별 INSERT 금지)
- 증분 집계 갱신
- 실패 시 재시도 → 초과하면 DLQ. DB 일시 오류는 ack 없이 같은 배치를 재시도하고, 독 메시지 · 전달 횟수 초과만 DLQ (10 §6)

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

- 같은 라우팅 키 → 항상 같은 파티션 → 한 워커가 순서대로 처리
- 파티션 수만큼 워커를 늘릴 수 있음 (그 이상은 늘려도 소용없음)
- 파티션 수는 **16으로 고정** (`p00`~`p15`). 예상 최대 워커 수(8)의 2배

라우팅 키는 큐마다 다르다.

| 큐 | 라우팅 키 | 이유 |
|---|---|---|
| `fleet.telemetry` | `robotId` | 로봇별 위치 순서 |
| `fleet.task` | `TaskCreated/Assigned/Started/Completed/Failed` → `taskId`, `RobotStatusChanged` → `robotId` | `TaskCreated`에는 아직 `robotId`가 없다. 작업 생애주기는 작업 단위로, 상태 전이는 로봇 단위로 순서를 지키면 충분 |
| `fleet.conflict` | `mapId` | 분석용. 로봇 간 순서 불필요 |

> **확장성과 순서 보장의 trade-off.** 파티션 수가 동시 처리 가능한 워커 수의
> 상한이 된다. 무한정 늘릴 수 없다는 것이 이 설계의 제약이며,
> 순서가 중요한 시스템에서 피할 수 없는 선택이다.

### 워커 수가 변할 때 (HPA)

파티션이 16개 고정인데 워커가 2 → 6 → 3으로 오르내리면 **누가 어느 파티션을 먹는지**가
바뀐다. 전환 중 두 워커가 같은 파티션을 동시에 소비하면 순서가 깨진다.

| 장치 | 역할 |
|---|---|
| 파티션 큐에 `x-single-active-consumer: true` | 한 큐에 소비자가 여럿 붙어도 **RabbitMQ가 1개만 활성**. 나머지는 대기. 활성 소비자가 떨어지면 다음 소비자가 넘겨받는다 |
| Redis 워커 명부 `worker:members` (Sorted Set, 10초 heartbeat, 30초 만료) | 각 워커가 자기 **순위(rank)와 총원(N)** 을 안다 |
| 결정적 배정 `partition % N == rank` | 워커가 자기 몫만 구독. 명부가 바뀌면 몫 밖 파티션은 **소비자 취소**, 새 몫은 구독 |

```
워커 2대: W0 → p00,p02,...,p14   W1 → p01,p03,...,p15
워커 3대: W0 → p00,p03,...        W1 → p01,p04,...       W2 → p02,p05,...
```

전환 순서가 핵심이다. 새 몫을 **먼저 구독**(SAC 때문에 대기 상태로 붙음)하고, 몫 밖 파티션을
**나중에 취소**한다. 취소 시점에 미확인(unacked) 메시지는 큐 앞으로 되돌아가고, 대기 중이던
소비자가 활성화되어 그 지점부터 이어받는다. 어느 순간에도 한 파티션의 활성 소비자는 1개다.

> **SAC만으로 안 되는 이유** — SAC는 "동시에 둘이 먹는 것"은 막지만 "누가 먹을지"는 먼저
> 붙은 순서로 정한다. 워커를 늘려도 기존 워커가 16개를 다 쥐고 있어 새 워커는 놀게 된다.
> 명부 기반 배정이 분배를, SAC가 전환 중 순서를 책임진다. 둘 다 필요하다.
>
> **명부 기반 배정만으로 안 되는 이유** — heartbeat 지연으로 두 워커가 잠깐 같은 rank를
> 계산할 수 있다. 그 순간을 SAC가 막는다. 명부는 "최선의 분배", SAC는 "안전장치"다.

프리페치는 파티션당 50. 처리 완료 순서대로 ack한다(건너뛰기 금지).

### 메시지 지속성

| 큐 | durable | 메시지 persistent | 비고 |
|---|---|---|---|
| telemetry | yes | **no** | 유실돼도 다음 틱에 갱신됨. 성능 우선 |
| task | yes | **yes** | 실적 데이터. 유실 불가 |
| conflict | yes | yes | 분석 자료 |

`task`·`conflict` 큐는 M3부터 **quorum queue** 타입으로 선언하고(멤버 1), M5에서 멤버 3으로 확장한다.
큐 타입은 선언 후 바꿀 수 없다. `telemetry`는 classic 비복제로 남긴다. → 07, 큐 인자 · 상한 · DLQ는 08 §8

---

## Redis 설계

### 키 구조

```
# 맵 판정 상태 (M4~) — 판정 안전에 필요한 것 전부. 필드 정의는 08 "맵 상태 단일 해시"
fleet:map:{mapId}                → Hash {epoch, wm, wd, rh, rec, c:{x}:{y}, r:{robotId}, wg, dl, yl, ...}   TTL 30s (커밋마다 갱신)

# 로봇 현재 상태 (조회용 캐시. 판정에 쓰지 않는다)
robot:state:{robotId}            → Hash {x, y, heading, status, taskId, battery, tick}

# 시공간 예약 (예약 방식 사용 시)
reserve:{mapId}:{x}:{y}:{tick}   → robotId          TTL 5s

# 멱등 키 (2단계)
idem:{batchId}                   → "pending:{unixMs}" | "done"   TTL 10m

# 워커 명부 (파티션 배정)
worker:members                   → Sorted Set {workerId → lastHeartbeatUnixMs}

# 작업 대기열
task:pending:{mapId}             → List<taskId>

# 집계 캐시
metrics:robot:{robotId}:today    → Hash {moveMs, waitMs, idleMs, taskCount}
metrics:heatmap:{mapId}          → Hash {"x:y" → count}
```

기존 `fleet:epoch:{mapId}`, `cell:lock:*`, `wait:*`, `deadlock:resolve:*`, `robot:yielding:*`는 맵 해시의 필드로 들어간다.

### 칸 점유 — 맵 단일 해시, 칸별 만료 없음

| 결정 | 근거 |
|---|---|
| 칸 점유는 맵 해시의 `c:{x}:{y}` 필드 | 락 · 해제 · 판정 기록 · 워터마크를 커밋 Lua 하나로 원자 반영한다 (08 §4) |
| **칸별 논리 만료(20틱) · 실시간 TTL 폐지** | 이동 요청을 보내지 않는 정지 로봇(Idle · Loading)의 칸이 20틱 뒤 만료되어 다른 로봇이 승인받는 결함이 있었다. 틱 정지가 실시간 TTL보다 길면 칸 락이 일부만 사라지는 구멍도 생긴다 |
| 해제는 명시적으로만 — 다음 배치의 `releases` | 해제도 같은 원자 커밋 안에서, 커밋 Lua가 기대값을 검사한 뒤 지운다. 토큰 검증이 필요 없다 |
| 맵 키 전체에 TTL 30초 | 버려진 맵 정리용. 단일 키라 통째로만 사라지므로 구멍이 없고, 사라지면 `409 missing` → 재주장 |

> 2칸 선점(04)은 그대로다. 이동 승인 시 목표 칸을 추가로 점유하고, 이동 완료 해제로 이전 칸을 비운다.

### Redis 상태 소실 복구 (FLUSHDB · 재시작 · 장애 조치)

Redis가 비거나 장애 조치로 최근 틱이 빠지면 그 상태로 이동 판정을 계속할 때
**비어 보이는 칸**에 로봇이 겹친다. 진실은 Simulator가 들고 있으므로 Simulator가 재주장한다.

| 단계 | 주체 | 동작 |
|---|---|---|
| 1 | FleetController | 배치 요청마다 맵 해시의 `epoch`와 체인 꼬리(`wm`, `wd`)를 읽는다. **키가 없거나, `epoch`가 다르거나, 꼬리가 요청의 `prev`와 다르면** 판정하지 않고 `409 state_lost` (08 "해시 체인 워터마크") |
| 2 | Simulator | 틱 진행을 멈춘다(로봇 전원 그 자리에 정지, `tick`은 증가하지 않음). 복구 중 틱은 `fleet_recovery_ticks_total`로 집계 |
| 3 | Simulator | `POST /api/v1/recovery/reclaim` — 모든 로봇의 `{robotId, position, nextCell?, heading, status, taskId}`, 새 `epoch`, 마지막으로 적용한 `tick` 전송 |
| 4 | FleetController | 맵 해시를 지우고 로봇 현재 칸과 (이동 중이면) 다음 칸을 전부 재설정, 체인 뿌리 기록. `robot:state` 복원 |
| 5 | Simulator | 응답의 `digest`를 `prev`로 삼아 새 `epoch`로 틱 재개. 대기 그래프는 비어 있지만 다음 틱의 거부에서 자연히 다시 쌓인다 |

- Simulator는 기동할 때도 재주장으로 시작한다. **Simulator 재시작 = 새 세대**
- 멱등 키도 함께 사라지므로 10분 창 안의 재전송은 Ingest를 통과한다 → 워커 2차 방어가 막는다 (아래)
- 재주장 API는 멱등이다. 같은 `epoch`로 두 번 와도 결과가 같다

> **왜 Simulator가 재주장하는가** — Redis는 캐시이고 FleetController는 무상태다.
> 로봇이 "실제로 어디 있는지"를 아는 건 Simulator뿐이다. 이 복구 절차가 있어야
> 02의 장애 주입 "Redis 플러시" 시나리오에서 로봇이 겹치지 않는다.

---

## PostgreSQL 설계

### 테이블 키 정책

| 구분 | PK | 이유 |
|---|---|---|
| **사실 · 이벤트** (`simulations` · `robot_telemetry` · `task_history` · `conflict_log` · `replan_log` · `metrics_minute` · `run_metrics`, 10의 영수증 · `robot_state`) | **자연키** (`run_id` · `event_id` · `robot_id` · `task_id` TEXT) | **멱등 키가 이벤트에서 온다.** 숫자 PK면 적재마다 코드→ID 조회가 필요하고, 기준정보가 없으면 **적재 실패 → DLQ**가 된다. 유실 0이 우선이다 |
| **기준정보** 7개 (`lines` · `equipment_types` · `equipment` · `maps` · `stations` · `task_patterns` · `task_pattern_flows`) | **숫자 대리키** `BIGINT GENERATED ALWAYS AS IDENTITY` + **비즈니스 코드 `*_code` UNIQUE** | 사람이 읽는 코드(`"EQ-01"`)는 바뀔 수 있다. 재명명해도 과거 이력이 따라오려면 PK가 불변이어야 한다 |

| 명명 | |
|---|---|
| 기준정보 숫자 PK | `line_id` · `equip_type_id` · `equip_id` · `map_id` · `station_id` · `pattern_id` · `flow_id` |
| 비즈니스 코드 | `line_code` · `equip_type_code` · `equip_code` · `map_code` · `station_code` · `pattern_code` |
| **사실 테이블의 맵 참조** | **`map_code TEXT`** — 기준정보의 숫자 `map_id`와 겹치지 않게 개명. **이벤트 봉투의 `mapId`(03)는 그대로**(외부 계약, Redis 키 · 큐 라우팅 키). 워커가 적재할 때 `map_code`에 넣는다 |

> **키에 `run_id`가 선두로 들어가는 이유** — `tick`과 `taskId`는 run마다 0부터 다시 시작한다.
> `run_id`가 없으면 `task_history`는 단조 upsert에 막혀 **조용히 무시**되고,
> `robot_state`는 이전 run의 상태 때문에 전이 검증이 전부 실패한다(10 §6).
> 실험 질의가 전부 "이 run의"로 시작하므로 **인덱스 선두**여야 한다.

### 사실 · 이벤트 테이블

```sql
-- run 메타 · 실험 조건. run_id는 Simulator가 만들어 03 봉투로 보내고, seq는 DB가 매기는 순번(보고서의 "N회차")
CREATE TABLE simulations (
    run_id        TEXT PRIMARY KEY,
    seq           BIGSERIAL UNIQUE,
    map_code      TEXT NOT NULL,
    environment   TEXT,              -- local | aws (03 SimulationStarted)
    line_code     TEXT,
    pattern_code  TEXT,
    map_version   INT,
    map_hash      TEXT,
    robots        INT,
    speed         REAL,
    tick_ms       INT,
    task_seed     BIGINT,
    params        JSONB,             -- 04 설정값 13종 스냅샷
    started_at    TIMESTAMPTZ,
    ended_at      TIMESTAMPTZ,
    ticks         BIGINT,
    tasks_created INT, tasks_completed INT, tasks_failed INT,
    overruns      BIGINT,
    end_reason    TEXT               -- max_ticks | stopped | faulted
);

-- 로봇 위치 이력 (대용량 · 파티셔닝)
CREATE TABLE robot_telemetry (
    id          BIGSERIAL,
    run_id      TEXT        NOT NULL,
    map_code    TEXT        NOT NULL,
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

-- 텔레메트리 중복 방지: run_id 선두, 파티션 키 occurred_at 포함 필수
CREATE UNIQUE INDEX ux_robot_telemetry_natural
    ON robot_telemetry (run_id, robot_id, tick, occurred_at);

-- 작업 이력
CREATE TABLE task_history (
    run_id       TEXT NOT NULL,
    task_id      TEXT NOT NULL,
    map_code     TEXT NOT NULL,
    robot_id     TEXT,
    task_type    SMALLINT NOT NULL,
    origin_x     SMALLINT, origin_y SMALLINT,
    dest_x       SMALLINT, dest_y SMALLINT,
    origin_station_code TEXT, dest_station_code TEXT,   -- 03 TransportTask 설비 식별자
    origin_equip_code   TEXT, dest_equip_code   TEXT,
    created_at   TIMESTAMPTZ NOT NULL,
    assigned_at  TIMESTAMPTZ,
    started_at   TIMESTAMPTZ,
    completed_at TIMESTAMPTZ,
    status       SMALLINT NOT NULL,
    path_length  INT,          -- 계획 경로 길이
    actual_steps INT,          -- 실제 이동 칸 수
    wait_ms      INT,          -- 충돌 대기 누적
    PRIMARY KEY (run_id, task_id)
);

-- 충돌 · 교착 이력. PK · UNIQUE는 그대로(event_id = {batchId}/{index}, batchId가 UUIDv7이라 run 간 충돌 없음)
CREATE TABLE conflict_log (
    id                BIGSERIAL PRIMARY KEY,
    event_id          TEXT NOT NULL UNIQUE,   -- "{batchId}/{index}" — 워커 2차 멱등
    run_id            TEXT NOT NULL,
    map_code          TEXT NOT NULL,
    tick              BIGINT NOT NULL,
    conflict_type     SMALLINT NOT NULL,   -- vertex / edge / following / deadlock
    x                 SMALLINT, y SMALLINT,
    robot_ids         TEXT[] NOT NULL,
    resolution        SMALLINT,            -- wait / reroute / yield / failed
    wait_ms           INT,                 -- 충돌 대기(ConflictDetected)
    resolved_ms       INT,                 -- 교착 해소 시간(DeadlockDetected) — 05 "교착 해소 시간"
    yield_robot_id    TEXT,
    yield_to_x        SMALLINT, yield_to_y SMALLINT,
    searched_distance SMALLINT,
    occurred_at       TIMESTAMPTZ NOT NULL
);
CREATE INDEX ix_conflict_log_run ON conflict_log (run_id);

-- 재계획 이력 (05 재계획률 · 실험 5 · M9 replan_storm)
CREATE TABLE replan_log (
    event_id    TEXT PRIMARY KEY,
    run_id      TEXT NOT NULL,
    map_code    TEXT NOT NULL,
    robot_id    TEXT NOT NULL,
    task_id     TEXT,
    tick        BIGINT NOT NULL,
    reason      SMALLINT NOT NULL,   -- blocked | deadlock | map_changed
    old_length  INT, new_length INT,
    compute_ms  REAL,
    occurred_at TIMESTAMPTZ NOT NULL
);
CREATE INDEX ix_replan_log_run ON replan_log (run_id);

-- 집계 (분 단위 롤업)
CREATE TABLE metrics_minute (
    run_id      TEXT NOT NULL,
    map_code    TEXT NOT NULL,
    robot_id    TEXT NOT NULL,
    minute      TIMESTAMPTZ NOT NULL,
    move_ms     INT NOT NULL DEFAULT 0,
    work_ms     INT NOT NULL DEFAULT 0,
    wait_ms     INT NOT NULL DEFAULT 0,
    idle_ms     INT NOT NULL DEFAULT 0,
    task_count  INT NOT NULL DEFAULT 0,
    PRIMARY KEY (run_id, robot_id, minute)
);

-- run 단위 결과 집계. simulations는 조건(입력), run_metrics는 결과 — 지표 정의가 바뀌어도 재집계만 한다
CREATE TABLE run_metrics (
    run_id        TEXT PRIMARY KEY,
    -- 시간 분해 (전체 로봇 합)
    move_ms BIGINT, work_ms BIGINT, wait_ms BIGINT, idle_ms BIGINT,
    utilization   REAL,   -- (move+work)/전체
    wait_ratio    REAL,   -- 혼잡 손실률
    idle_ratio    REAL,
    -- 대기
    wait_count    INT,    -- conflict_log 건수(대기 구간당 ConflictDetected 1건)
    wait_ms_avg   REAL,   -- wait_ms / wait_count
    -- 작업
    throughput_per_hour REAL,
    lead_time_p50 INT, lead_time_p90 INT, lead_time_p99 INT,
    path_efficiency REAL, -- SUM(path_length)/SUM(actual_steps)
    task_failed_ratio REAL,
    -- 알고리즘 품질
    conflict_count INT, deadlock_count INT, deadlock_resolve_ms_avg REAL,
    replan_count INT, replan_per_task REAL,
    -- 시스템
    tick_overruns BIGINT, stall_ms BIGINT,
    computed_at   TIMESTAMPTZ
);
```

| 결정 | 근거 |
|---|---|
| `simulations`는 **워커가** `SimulationStarted` · `SimulationEnded`로 upsert | Simulator는 PostgreSQL에 붙지 않는다(단방향). run 등록도 이벤트로 한다 |
| **DB가 run 번호를 매기지 않는다** — `seq`는 보고서용 순번일 뿐 | 워커가 적재 시점에 "현재 run"을 조회하면 ① 송신 버퍼 60초 때문에 전환 경계가 섞이고 ② 워커 2대가 다른 시점에 조회하며 ③ 멱등 재처리 때 같은 `eventId`에 다른 run이 붙는다 |
| `map_code`는 PK에서 빠지지만 **컬럼으로 남긴다** | `run_id`가 맵을 결정한다. Reconcile telemetry 질의가 쓴다 |
| `resolvedMs`를 `wait_ms`에 넣지 않는다 | 충돌 대기와 의미가 다르다. 05가 "교착 해소 시간 = 평균 `resolvedMs`"를 알고리즘 품질의 직접 지표로 둔다 |
| `run_metrics`의 `wait_ms_avg`를 따로 둔다 | 총 대기가 같아도 "짧게 자주"(혼잡)와 "길게 가끔"(교착 · 양보 실패)은 처방이 다르다 |
| `run_metrics` 집계 시점 = **Reconcile 차집합 0 확인 뒤** | `SimulationEnded` 직후에는 다른 파티션의 telemetry가 아직 적재 중일 수 있다(10 §10) |

이벤트 영수증(`task_event_log` · `robot_status_log`), 전이 검증 상태(`robot_state`), DEFAULT 파티션, 마이그레이션 이력은 10 §8.

### 기준정보 — 라인 · 맵 · 설비 · 작업

03 맵 파일의 `stations` · `equipmentId`는 이미 있지만 `equipmentId`의 **마스터**가 없고, 작업은 **좌표만** 든다.
05 실험 4(맵 3종 비교)는 맵이 바뀌면 좌표가 바뀌므로 **같은 설비 쌍**의 작업이 재현돼야 조작 변수가 통로 폭 하나가 된다.

```
line (라인)
 ├─ map (맵, 버전)
 │   ├─ station  ──► equipment ──► equipment_type
 │   ├─ charger
 │   └─ parking
 └─ task_pattern (공정 흐름)
     └─ flow: from_equipment → to_equipment, 비율, 우선순위
```

```sql
CREATE TABLE lines (
    line_id     BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    line_code   TEXT NOT NULL,                    -- "L-A"
    name        TEXT NOT NULL,
    description TEXT,
    active      BOOLEAN NOT NULL DEFAULT true,
    CONSTRAINT uq_lines_code UNIQUE (line_code),
    CONSTRAINT uq_lines_name UNIQUE (name)
);

CREATE TABLE equipment_types (
    equip_type_id   BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    equip_type_code TEXT NOT NULL,                -- "press" | "inspect" | "buffer"
    name            TEXT NOT NULL,
    default_work_ms INT  NOT NULL DEFAULT 2000,
    CONSTRAINT uq_equip_types_code UNIQUE (equip_type_code),
    CONSTRAINT uq_equip_types_name UNIQUE (name),
    CONSTRAINT ck_equip_types_work CHECK (default_work_ms >= 0)
);

CREATE TABLE equipment (
    equip_id      BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    equip_code    TEXT   NOT NULL,                -- "EQ-01" — 맵 파일이 쓰는 값
    line_id       BIGINT NOT NULL REFERENCES lines(line_id),
    equip_type_id BIGINT NOT NULL REFERENCES equipment_types(equip_type_id),
    name          TEXT   NOT NULL,
    work_ms       INT    NOT NULL DEFAULT 2000,
    capacity      INT    NOT NULL DEFAULT 1,
    active        BOOLEAN NOT NULL DEFAULT true,
    CONSTRAINT uq_equipment_code     UNIQUE (equip_code),
    CONSTRAINT uq_equipment_name     UNIQUE (line_id, name),   -- 라인 안에서 설비명 유일
    CONSTRAINT ck_equipment_capacity CHECK (capacity >= 1),
    CONSTRAINT ck_equipment_work     CHECK (work_ms  >= 0)
);

CREATE TABLE maps (
    map_id      BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    map_code    TEXT   NOT NULL,                  -- "factory-a" — 이벤트의 mapId
    version     INT    NOT NULL,
    line_id     BIGINT NOT NULL REFERENCES lines(line_id),
    name        TEXT   NOT NULL,
    width       SMALLINT NOT NULL, height SMALLINT NOT NULL,
    cell_size_m REAL   NOT NULL DEFAULT 1.0,
    grid        JSONB  NOT NULL,
    map_hash    TEXT   NOT NULL,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT uq_maps_code_version UNIQUE (map_code, version),   -- 같은 맵의 같은 버전 금지
    CONSTRAINT uq_maps_code_hash    UNIQUE (map_code, map_hash),  -- 같은 내용을 두 버전으로 금지
    CONSTRAINT uq_maps_line_name_ver UNIQUE (line_id, name, version),
    CONSTRAINT ck_maps_size    CHECK (width > 0 AND height > 0 AND cell_size_m > 0),
    CONSTRAINT ck_maps_version CHECK (version >= 1)
);

CREATE TABLE stations (
    station_id   BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    station_code TEXT   NOT NULL,                 -- "ST-01" | "CH-01" | "PK-01"
    map_id       BIGINT NOT NULL REFERENCES maps(map_id) ON DELETE CASCADE,
    kind         SMALLINT NOT NULL,               -- 2=Station 3=Charge 4=Parking (03)
    equip_id     BIGINT REFERENCES equipment(equip_id),   -- Charge · Parking 은 NULL
    io_type      TEXT,                            -- pickup | dropoff
    x SMALLINT NOT NULL, y SMALLINT NOT NULL,
    CONSTRAINT uq_stations_code UNIQUE (map_id, station_code),
    CONSTRAINT uq_stations_cell UNIQUE (map_id, x, y),   -- 한 칸에 접점 둘 금지
    CONSTRAINT ck_stations_kind CHECK (kind IN (2, 3, 4)),
    CONSTRAINT ck_stations_xy   CHECK (x >= 0 AND y >= 0),
    CONSTRAINT ck_stations_equipment CHECK (
        (kind = 2 AND equip_id IS NOT NULL AND io_type IN ('pickup','dropoff'))
     OR (kind IN (3,4) AND equip_id IS NULL AND io_type IS NULL))
);

CREATE TABLE task_patterns (
    pattern_id   BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    pattern_code TEXT   NOT NULL,                 -- "P-A-standard"
    line_id      BIGINT NOT NULL REFERENCES lines(line_id),
    name         TEXT   NOT NULL,
    active       BOOLEAN NOT NULL DEFAULT true,
    CONSTRAINT uq_patterns_code UNIQUE (pattern_code),
    CONSTRAINT uq_patterns_name UNIQUE (line_id, name)
);

CREATE TABLE task_pattern_flows (
    flow_id        BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    pattern_id     BIGINT NOT NULL REFERENCES task_patterns(pattern_id) ON DELETE CASCADE,
    seq            INT    NOT NULL,
    from_equip_id  BIGINT NOT NULL REFERENCES equipment(equip_id),
    to_equip_id    BIGINT NOT NULL REFERENCES equipment(equip_id),
    weight         REAL     NOT NULL DEFAULT 1,
    priority       SMALLINT NOT NULL DEFAULT 0,   -- 0=normal 1=high (03)
    CONSTRAINT uq_flow_seq      UNIQUE (pattern_id, seq),
    CONSTRAINT uq_flow          UNIQUE (pattern_id, from_equip_id, to_equip_id, priority),
    CONSTRAINT ck_flow_distinct CHECK (from_equip_id <> to_equip_id),
    CONSTRAINT ck_flow_weight   CHECK (weight > 0),
    CONSTRAINT ck_flow_priority CHECK (priority IN (0, 1))
);
```

| 테이블 | 중복 금지 | 근거 |
|---|---|---|
| `lines` | 코드 · 라인명 | |
| `equipment_types` | 코드 · 종류명 | |
| `equipment` | 코드 / **`(라인, 설비명)`** | 설비 코드는 전역 유일, 설비명은 **라인 안에서** 유일 |
| `maps` | **`(map_code, version)`** / **`(map_code, map_hash)`** | 같은 버전 재등록 금지 · 같은 내용을 두 버전으로 올리는 실수 금지 |
| **`stations`** | **`(map_id, x, y)`** / `(map_id, station_code)` | **한 칸에 접점이 둘일 수 없다** |
| `task_patterns` | 코드 / `(라인, 패턴명)` | |
| **`task_pattern_flows`** | `(패턴, seq)` / **`(패턴, from, to, 우선순위)`** | 같은 흐름 중복 정의 금지. 우선순위가 다르면 별개 |

| CHECK | 내용 |
|---|---|
| **`ck_flow_distinct`** | **`from ≠ to`** — 03의 "출발지 · 목적지가 같으면 재추첨"을 DB가 강제한다 |
| `ck_stations_equipment` | `kind=Station`이면 설비 · `io_type` 필수, `Charge` · `Parking`이면 둘 다 NULL |
| `ck_stations_kind` · `ck_flow_priority` | 2 · 3 · 4 / 0 · 1 — **03의 enum과 일치** |
| `ck_equipment_capacity` · `ck_maps_size` · `ck_flow_weight` | `>= 1` · `> 0` |

> **크로스 테이블 검증은 CHECK로 못 한다**(`stations.x < maps.width` 같은 것). 적재 시점에는 시드 스크립트가,
> 이후에는 맵 검증기(06 맵 검증 규칙과 같은 검사 + 연결성 · 1칸 통로 · 막다른 길 깊이 · Parking 수 ≥ 로봇 수)가 맡는다.

| FK | 걸까 | 이유 |
|---|---|---|
| **기준정보끼리** | **건다** | 시드 스크립트로 미리 일괄 적재되므로 순서가 보장된다 |
| **큐에서 적재되는 테이블(`simulations` · `task_history` 등) → 기준정보** | **걸지 않는다** | 기준정보가 없으면 FK 위반 → 반환 → **DLQ**. 대신 코드를 문자열로 저장 + `mapHash` 대조 + **경고 로그** |
| `ON DELETE CASCADE` | **맵 하위(`stations`)에만** | `equipment` · `lines` · `task_patterns`는 **삭제하지 않는다** — 과거 run의 해석이 깨진다. 쓰지 않게 되면 `active = false` |

| 역할 | 어디 |
|---|---|
| **정본** | `maps/*.json` · `master/*.json` — git에서 버전 관리. 맵 파일 구조는 바꾸지 않는다 |
| 적재본 | 위 기준정보 테이블 — 질의 · 조인 · 재생기용 |
| 적재 경로 | 마이그레이션 / 시드 스크립트 (`db/seed/`) |
| run과의 대조 | `SimulationStarted.payload`의 `lineCode` · `mapId` · `mapVersion` · **`mapHash`** · `taskPatternCode`(03). DB 해시와 다르면 경고 |

분석 질의는 코드로 조인한다.

```sql
SELECT e.name, COUNT(*) AS tasks
FROM   task_history t
JOIN   equipment    e ON e.equip_code = t.dest_equip_code
WHERE  t.run_id = $1
GROUP  BY e.name;
```

> **`run_id` · 설비 코드 · 기준정보는 M3 마이그레이션(`001_schema.sql`)에 함께 넣는다.** M7에서 넣으면 PK 드롭 + 백필 + 재생성을
> 파티션마다 반복해야 하고, 그때까지 쌓인 데이터는 어느 run · 어느 설비였는지 알 수 없어 **백필 자체가 불가능**하다.
> **로봇 수 변경 기능(run 전환)은 이 작업이 끝난 뒤에만 구현한다** — 먼저 넣으면 위 두 증상(`robot_state` 전원 invalid,
> `task_history` 조용한 오염)이 그대로 발생한다.

### 텔레메트리 적재 전략

초당 수천 건을 개별 INSERT하면 DB가 먼저 죽는다.

- 다중 행 INSERT + `ON CONFLICT DO NOTHING`으로 **배치 삽입** (100~500건 묶음). `COPY`는 중복 처리가 안 되어 쓰지 않는다
- 인덱스 최소화 — 조회는 주로 집계 테이블에서
- 일 단위 파티션, 보존 기간 지나면 `DROP PARTITION` (DELETE 금지)

---

## 멱등 처리

Simulator는 틱마다 배치 1건을 보내고, 배치는 `batchId`(UUID v7)를 갖는다.
배치 안의 항목은 `eventId = "{batchId}/{index}"` 로 식별된다 (03 참조).
Ingest의 멱등 단위는 **배치**, 워커의 멱등 단위는 **항목**이다.

### Ingest — 2단계 상태 키

단순히 `SET NX` 후 발행하면, **발행이 실패했을 때 키는 남고 메시지는 없는** 상태가 되어
Simulator의 재전송이 "이미 처리됨"으로 버려진다. 유실이다. 그래서 키에 상태를 둔다.

```
1. SET idem:{batchId} "pending:{nowMs}" NX EX 600
   ├─ 성공               → 2로
   ├─ 실패, 값 == "done" → 202 (중복. 큐에 넣지 않음)
   ├─ 실패, 값 == "pending:{t}"
   │    ├─ now − t < 5s   → 503 Retry-After: 1   (다른 인스턴스가 발행 중)
   │    └─ now − t ≥ 5s   → 고아 pending. Lua로 값을 "pending:{nowMs}"로 갈아끼운 뒤 2로
   └─ Redis 오류         → 1단계를 건너뛰고 2로 (fail-open, fleet_ingest_idem_bypass_total)
2. 항목을 큐로 발행 (publisher confirm 대기 — task · conflict 2초, telemetry 200ms)
   ├─ task · conflict 전부 성공 → SET idem:{batchId} "done" XX EX 600 → 202
   │    (telemetry 실패분은 버리고 fleet_telemetry_dropped_total만 올린다)
   └─ task · conflict 하나라도 실패 → Lua로 "내가 쓴 pending일 때만" DEL → 503
      (Simulator가 같은 batchId로 재전송)
```

상태 전이 전체 표와 책임 분담은 08 §9.

| 결정 | 근거 |
|---|---|
| **telemetry 발행 실패는 배치 실패가 아니다** | 노드 다운으로 telemetry 파티션이 비면 task 이벤트까지 503에 묶인다(07). telemetry는 유실 허용 |
| Redis 오류 시 1단계 생략 | `idem:*`은 유실 허용이고 중복은 워커가 막는다. Redis 장애 조치 동안 파이프라인을 세우지 않는다 |
| Simulator 이벤트 송신은 **배치 하나씩 순서대로** | T가 503이고 T+1이 먼저 들어가면 같은 로봇의 이벤트 순서가 파티션에서 뒤바뀐다. 202를 받아야 다음 배치 |
| 발행 실패 시 키 삭제 | 재전송이 다시 1단계부터 통과해야 한다 |
| 그래도 `pending`이 남을 수 있음 (DEL 전에 Ingest가 죽음) | 5초 지난 pending은 고아로 보고 재점유. 5초는 발행 타임아웃(2초)의 2배 이상 |
| 부분 발행 후 재전송 → 일부 항목이 **두 번** 큐에 들어감 | 피할 수 없다. 워커 2차 방어가 항목 단위로 막는다 |
| 고아 재점유 중 원래 인스턴스가 살아서 발행을 마칠 수도 있음 | 역시 중복 발행 → 워커가 막는다. Ingest는 "유실 0"을, 워커는 "중복 0"을 책임진다 |

**왜 Ingest 단계에서 거르는가** — 워커에서 거르면 중복 메시지가 큐를 거쳐
워커까지 가서 버려진다. 앞단에서 막는 게 자원을 아낀다.

**TTL 10분의 근거** — 재전송은 네트워크 오류 복구 과정에서 수 초~수 분 내에 발생한다.
그보다 오래된 중복은 실질적으로 없다. TTL이 길면 Redis 메모리가 계속 증가한다.

### 워커 — 테이블별 2차 방어

Ingest가 뚫리는 경로가 셋 있다. 부분 발행 재전송, Redis 플러시로 멱등 키 소실, 워커 재시작으로
ack 전 메시지 재전달. 세 경우 모두 **같은 `eventId`가 두 번 온다**. 테이블마다 막는다.

| 테이블 | 유니크 키 | 충돌 시 | 근거 |
|---|---|---|---|
| `task_history` | `(run_id, task_id)` | `ON CONFLICT DO UPDATE ... WHERE excluded.status > task_history.status` | 작업은 상태가 단조 증가. 늦게 온 낮은 상태는 무시. 키에 `run_id`가 들어가 run 간 충돌은 없다 |
| `conflict_log` | `event_id` (변경 없음) | `ON CONFLICT DO NOTHING` | 같은 충돌을 두 번 세면 교착 발생률이 틀어진다 |
| `replan_log` | `event_id` | `ON CONFLICT DO NOTHING` | 재계획률(05)이 부풀지 않게 |
| `robot_telemetry` | `(run_id, robot_id, tick, occurred_at)` | `ON CONFLICT DO NOTHING` | 자연 키. 한 로봇은 run · 틱당 1행. `occurred_at`은 파티션 키라 포함해야 함 |
| `metrics_minute` | `(run_id, robot_id, minute)` | 같은 트랜잭션의 원본 INSERT `ON CONFLICT DO NOTHING RETURNING`으로 **새로 들어간 행만** 가산 (10 §6) | 집계는 되돌릴 수 없으므로 적용 전에 거른다. 별도 적용 집합 없이 원본 행 삽입과 원자적 |

> **텔레메트리까지 막는 이유** — "유실 허용"이지 "중복 허용"이 아니다. 중복 행은
> 통행량 히트맵과 시간 분해를 부풀린다. 자연 키 제약은 배치 INSERT에서 `ON CONFLICT`
> 한 줄이라 비용이 작다. 단, `COPY`는 `ON CONFLICT`를 못 쓰므로 텔레메트리 적재는
> **다중 행 INSERT**로 간다.

---

## 서비스 간 통신 요약

| 구간 | 방식 | 이유 |
|---|---|---|
| Simulator → FleetController | HTTP 동기, **틱당 배치 1건**. headless Service, 성공 인스턴스 고정 · 실패 시 다른 인스턴스로 재전송 | 즉답 필요 (이동 승인). 건별 호출은 틱 예산 초과 (08 §5) |
| FleetController ↔ Redis | TCP, Sentinel 경유. 배치당 읽기 Lua 1회 + 커밋 Lua 1회(+`WAIT 1`) | 저지연 상태 조회, 판정 기록 원자화 (08 §2) |
| Simulator → IngestApi | HTTP 배치, 틱당 1건, 순차 송신 | 그 틱의 모든 이벤트를 묶어 전송. 유일한 이벤트 발행 경로 |
| IngestApi → RabbitMQ | AMQP | 비동기 분리 |
| RabbitMQ → EventWorker | AMQP | 파티션별 순서 보장 |
| QueryApi → 관제 화면 | WebSocket | 실시간 위치 푸시 |
