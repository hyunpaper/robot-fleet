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
   │   │  (API×N)     │        └─────────┘
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

**이벤트는 발행하지 않는다.** 판정 결과(충돌·교착·재계획 정보)를 응답에 실어 돌려주고,
이벤트로 바꿔 발행하는 일은 Simulator가 한다. (→ 03 "이벤트 발행 주체")

#### 틱 단위 배치 이동 판정 API

```
POST /api/v1/move-requests/batch
{
  "mapId": "factory-a",
  "tick": 142057,
  "epoch": "e-7f3a",                       // Redis 상태 세대 (→ "Redis 상태 소실 복구")
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
  "decisions": [
    { "robotId": "R-012", "granted": true,  "lockToken": "R-012:142057:9f1c" },
    { "robotId": "R-034", "granted": false, "reason": "occupied", "blockedBy": "R-012",
      "suggestedAction": "wait" },
    { "robotId": "R-007", "granted": false, "reason": "deadlock",  "suggestedAction": "yield",
      "yieldTo": { "x": 24, "y": 16 },
      "deadlock": { "cycle": ["R-007","R-012","R-034"], "cells": [ ... ] } }
  ]
}

409 { "error": "state_lost", "epoch": null }   // Redis 비어 있음 → 복구 절차 진입
```

요청 1건에 그 틱에 움직이려는 로봇 전부가 들어간다(상한 1000). 단건 `POST /api/v1/move-request`는
디버깅용으로만 남기고 Simulator는 쓰지 않는다.

#### 배치 내 판정 순서

같은 배치 안의 두 로봇이 같은 칸(정점 충돌)이나 서로의 칸(교차 충돌)을 노릴 수 있다.
Redis에 가서 경쟁시키지 않고 **메모리에서 먼저 정렬·판정한 뒤 Redis에는 결과만 반영**한다.

| 단계 | 내용 |
|---|---|
| 1 | 요청을 04의 우선순위 점수로 **내림차순 정렬**. 동점이면 `robotId` 오름차순 (결정적) |
| 2 | 정렬 순서대로 순차 판정. 배치 내 선행 로봇이 이미 차지한 `to` 칸은 점유로 취급 |
| 3 | 교차 판정: `A.to == B.from && B.to == A.from`이면 후순위 로봇 거부 (`reason: swap`) |
| 4 | 메모리 통과분만 Redis Lua 스크립트 1회로 일괄 락 획득 (아래 "셀 점유 락") |
| 5 | Lua에서 실패한 로봇(다른 pod·이전 틱 잔여 락)은 `occupied`로 응답 |
| 6 | 거부된 로봇의 대기 관계를 `wait:{robotId}`에 기록하고 교착 탐지 실행 (04) |

> 2칸 선점(04)이라 맞바꿈 쌍은 선순위 로봇도 상대의 현재 칸에 막혀 `occupied`가 된다.
> 후순위가 `swap`으로 응답되도록 구현은 로봇마다 3단계(교차)를 2단계(점유)보다 먼저 검사한다.
> 거부 응답의 `blockedBy`는 막고 있는 로봇이며, 6단계 대기 관계의 입력이다.

> **왜 메모리에서 먼저 판정하는가** — 배치 안의 충돌은 전부 같은 pod가 보고 있으므로
> Redis 왕복 없이 결정할 수 있다. Redis 왕복은 배치당 1회(Lua)로 고정되어
> 로봇 수가 늘어도 판정 지연이 선형으로 늘지 않는다. 정렬이 결정적이어야
> 같은 입력을 재전송했을 때 같은 결과가 나온다(멱등).

한 틱의 배치는 하나의 pod가 통째로 처리한다. 로드밸런서가 틱마다 다른 pod로 보내도
상태는 전부 Redis에 있으므로 문제없다.

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

M5 이후 `task` 큐를 **quorum queue**로 전환한다.
→ 단일 노드 → 복제 3노드로 바꾸는 과정 자체를 기록으로 남긴다.

---

## Redis 설계

### 키 구조

```
# 상태 세대 (FLUSHDB 감지용)
fleet:epoch:{mapId}              → epochId          TTL 없음

# 셀 점유 락 (TTL 필수) — 값에 틱을 함께 기록
cell:lock:{mapId}:{x}:{y}        → "{robotId}:{tick}:{token}"   TTL = 실시간 환산값 (아래)

# 로봇 현재 상태
robot:state:{robotId}            → Hash {x, y, heading, status, taskId, battery, tick}

# 대기 그래프 (교착 탐지)
wait:{mapId}:{robotId}           → "{blockedByRobotId}:{tick}"   TTL 5s

# 교착 해소 락 (04 참조)
deadlock:resolve:{mapId}:{cycleKey}  → "{resolverPod}:{tick}:{yieldRobotId}"   TTL 50틱

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

### 락 만료 — 논리 시간과 실시간을 둘 다 쓴다

로봇이 셀을 잠근 채 프로세스가 죽으면 **그 칸은 영원히 통행 불가**가 된다.
맵 중앙 통로에서 이런 일이 생기면 공장 전체가 멈춘다. 그래서 TTL이 필수인데,
"실시간 2초 고정"은 배속과 어긋난다. 배속 10이면 실시간 2초는 틱 200회분이라
죽은 로봇의 칸이 논리 시간으로 20초나 막히고, 배속 0.1이면 틱 2회 만에 산 로봇의 락이 날아간다.

| 기준 | 값 | 역할 |
|---|---|---|
| **논리 만료** | `lockTtlTicks = 20` | **정상 판정 기준.** 락 값의 `tick`이 `현재 tick − 20`보다 오래되면 만료로 간주하고 덮어쓴다 |
| **실시간 TTL** | `clamp(20 × tickMs ÷ speedFactor, 1s, 30s)` | **안전망.** Simulator 자체가 죽어 틱이 멈추면 논리 만료는 영원히 안 오므로, 실시간으로도 반드시 풀린다 |

```
speedFactor 1   → TTL 2s     (틱 20회 = 2초)
speedFactor 10  → TTL 1s     (계산값 0.2s → 하한 1s로 보정)
speedFactor 0.5 → TTL 4s
```

- Simulator가 `speedFactor`를 배치 요청의 설정으로 전달하고, FleetController는 그 값으로 TTL을 계산
- 정상 이동 완료 시 즉시 해제, 매 틱 승인 시 `tick`·TTL 갱신(연장)
- 실시간 TTL 하한 1초: 배속이 높을 때 네트워크 지연 한 번에 산 로봇의 락이 사라지는 것을 막는다

> **논리 만료가 주, 실시간이 보조인 이유** — 충돌 판정은 전부 틱 세계에서 일어난다.
> "20틱 동안 갱신이 없으면 죽은 것"이 배속과 무관하게 참이다. 실시간 TTL은
> 틱 자체가 멈춘 상황(Simulator 다운)만 책임지므로 넉넉하게 잡아도 된다.

### 일괄 락 획득 (Lua)

배치 판정 4단계에서 호출한다. 키마다 "비어 있음 / 내 것 / 논리 만료"면 획득, 아니면 실패.

```lua
-- KEYS: cell:lock 키들, ARGV[1]=robotId 목록(JSON), ARGV[2]=tick, ARGV[3]=lockTtlTicks, ARGV[4]=ttlMs
local results = {}
for i, key in ipairs(KEYS) do
    local v = redis.call("GET", key)
    local ok = (v == false)
    if not ok then
        local owner, t = v:match("^([^:]+):(%d+):")
        ok = (owner == robotIds[i]) or (tonumber(t) + lockTtlTicks < tick)
    end
    if ok then
        redis.call("SET", key, robotIds[i] .. ":" .. tick .. ":" .. token, "PX", ttlMs)
    end
    results[i] = ok and 1 or 0
end
return results
```

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

### Redis 상태 소실 복구 (FLUSHDB · 재시작)

Redis가 비면 락·로봇 상태·대기 그래프가 전부 사라진다. 그 상태로 이동 판정을 계속하면
**모든 칸이 비어 보여** 로봇이 서로 겹친다. 진실은 Simulator가 들고 있으므로 Simulator가 재주장한다.

| 단계 | 주체 | 동작 |
|---|---|---|
| 1 | FleetController | 배치 요청마다 `fleet:epoch:{mapId}`를 읽는다. **없거나 요청의 `epoch`와 다르면** 판정하지 않고 `409 state_lost` |
| 2 | Simulator | 틱 진행을 멈춘다(로봇 전원 그 자리에 정지, `tick`은 증가하지 않음). 복구 중 틱은 `fleet_recovery_ticks_total`로 집계 |
| 3 | Simulator | `POST /api/v1/recovery/reclaim` — 모든 로봇의 `{robotId, position, nextCell?, heading, status, taskId}` 와 새 `epoch` 전송 |
| 4 | FleetController | 로봇 현재 칸과 (이동 중이면) 다음 칸의 락을 **현재 tick으로 전부 재설정**, `robot:state` 복원, `fleet:epoch` 기록 |
| 5 | Simulator | 응답을 받으면 새 `epoch`로 틱 재개. 대기 그래프는 비어 있지만 다음 틱의 거부에서 자연히 다시 쌓인다 |

- `fleet:epoch`는 Simulator가 기동할 때도 한 번 등록한다. **Simulator 재시작 = 새 세대**
- 멱등 키도 함께 사라지므로 10분 창 안의 재전송은 Ingest를 통과한다 → 워커 2차 방어가 막는다 (아래)
- 재주장 API는 멱등이다. 같은 `epoch`로 두 번 와도 결과가 같다

> **왜 Simulator가 재주장하는가** — Redis는 캐시이고 FleetController는 무상태다.
> 로봇이 "실제로 어디 있는지"를 아는 건 Simulator뿐이다. 이 복구 절차가 있어야
> 02의 장애 주입 "Redis 플러시" 시나리오에서 로봇이 겹치지 않는다.

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

-- 텔레메트리 중복 방지 (파티션 키 포함 필수)
CREATE UNIQUE INDEX ux_robot_telemetry_natural
    ON robot_telemetry (map_id, robot_id, tick, occurred_at);

-- 충돌 이력
CREATE TABLE conflict_log (
    id            BIGSERIAL PRIMARY KEY,
    event_id      TEXT NOT NULL UNIQUE,   -- "{batchId}/{index}" — 워커 2차 멱등
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
   └─ 실패, 값 == "pending:{t}"
        ├─ now − t < 5s   → 503 Retry-After: 1   (다른 인스턴스가 발행 중)
        └─ now − t ≥ 5s   → 고아 pending. Lua로 값을 "pending:{nowMs}"로 갈아끼운 뒤 2로
2. 항목을 큐로 발행 (publisher confirm 대기)
   ├─ 전부 성공 → SET idem:{batchId} "done" XX EX 600 → 202
   └─ 하나라도 실패 → Lua로 "내가 쓴 pending일 때만" DEL → 503
      (Simulator가 같은 batchId로 재전송)
```

| 결정 | 근거 |
|---|---|
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
| `task_history` | `task_id` | `ON CONFLICT DO UPDATE ... WHERE excluded.status > task_history.status` | 작업은 상태가 단조 증가. 늦게 온 낮은 상태는 무시 |
| `conflict_log` | `event_id` | `ON CONFLICT DO NOTHING` | 같은 충돌을 두 번 세면 교착 발생률이 틀어진다 |
| `robot_telemetry` | `(map_id, robot_id, tick, occurred_at)` | `ON CONFLICT DO NOTHING` | 자연 키. 한 로봇은 틱당 1행. `occurred_at`은 파티션 키라 포함해야 함 |
| `metrics_minute` | `(map_id, robot_id, minute)` | 증분 갱신 전 `event_id`를 `metrics_applied` 집합(Redis Set, TTL 10m)으로 확인 | 집계는 되돌릴 수 없으므로 적용 전에 거른다 |

> **텔레메트리까지 막는 이유** — "유실 허용"이지 "중복 허용"이 아니다. 중복 행은
> 통행량 히트맵과 시간 분해를 부풀린다. 자연 키 제약은 배치 INSERT에서 `ON CONFLICT`
> 한 줄이라 비용이 작다. 단, `COPY`는 `ON CONFLICT`를 못 쓰므로 텔레메트리 적재는
> **다중 행 INSERT**로 간다.

---

## 서비스 간 통신 요약

| 구간 | 방식 | 이유 |
|---|---|---|
| Simulator → FleetController | HTTP 동기, **틱당 배치 1건** | 즉답 필요 (이동 승인). 건별 호출은 틱 예산 초과 |
| FleetController ↔ Redis | TCP, 배치당 Lua 1회 | 저지연 상태 조회 |
| Simulator → IngestApi | HTTP 배치, 틱당 1건 | 그 틱의 모든 이벤트를 묶어 전송. 유일한 이벤트 발행 경로 |
| IngestApi → RabbitMQ | AMQP | 비동기 분리 |
| RabbitMQ → EventWorker | AMQP | 파티션별 순서 보장 |
| QueryApi → 관제 화면 | WebSocket | 실시간 위치 푸시 |
