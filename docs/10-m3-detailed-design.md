# 10. M3 상세 설계 — 이벤트 파이프라인

09의 큰 틀 아래 위임 항목 1~14를 채운다. 값마다 근거를 한 줄로 붙이고, 측정으로 확정할 값은 **M3 측정**으로 표시한다.

---

## 09 대비 변경점

| # | 09 원문 | 10 결정 | 근거 |
|---|---|---|---|
| D1 | 메시징 라이브러리 MassTransit 기본 | **RabbitMQ.Client 7 직접 사용** | §3 항목별 표. 01 토폴로지 7개 중 4개가 MassTransit 기본 동작과 정면으로 부딪친다(봉투 · 오류 큐 · 처리 순서 ack · 반환 의미론) |
| D2 | 인프라 인터페이스 5종 | **6종** — `IPartitionSubscriber`(소비 쪽) 추가 | `IEventPublisher`는 발행만 다룬다. 워커의 구독 · SAC · ack · 반환은 경계가 따로 있어야 한다 |
| D3 | 파티션 생성은 기동 시와 자정 | 기동 시 + **매시 정각**, 그리고 **DEFAULT 파티션** | 자정 타이머 하나는 그 순간 재시작 중이면 놓친다. 매시 확인은 멱등이라 비용이 없다. DEFAULT는 그래도 빠진 날짜의 행을 받아 INSERT 실패(=유실)를 원천 차단 |
| D4 | (01) `metrics_minute` 멱등은 Redis `metrics_applied` 집합 | **같은 PG 트랜잭션의 `INSERT … ON CONFLICT DO NOTHING RETURNING`** 결과로만 집계 | M3의 Redis는 선택이다(09). 실제로 새로 들어간 행만 집계하면 별도 집합 없이 정확히 1회. 집합 기록과 집계가 같은 트랜잭션이라 사이에서 죽어도 어긋나지 않는다 |
| D5 | (09) EventWorker는 HTTP 없음 | 업무 API는 없고 **관리 포트**(`/metrics` · `/health/*`)만. **Simulator에도 관리 포트**를 두고 run 전환 `POST /admin/run`을 연다(02 "run 관리"). **둘 다 인그레스 · 터널에 올리지 않는다** | 09 완료 기준 2~6의 메트릭 출처이고 readiness가 필요하다. 로봇 수 · 배속을 외부에서 바꿀 수 있으면 실험이 통째로 무의미해진다 |
| D7 | (09) L1 인메모리 fake · in-process 모드 · 계약 테스트 InMemory 실행 | **대역 없음.** 인프라가 닿는 검증은 실제 RabbitMQ · Redis · PostgreSQL 통합 테스트만(§12). `Infrastructure.InMemory` · `PipelineHost` 삭제 | 사용자 지시. 대역이 실제와 다르면 거짓 안심이 된다. 서버가 없으면 통합 테스트는 skip |
| D6 | (09) 적재 집합 = `task_history` 전이 · `conflict_log` | `task_event_log` · `robot_status_log`(이벤트당 1행, `event_id` PK) 추가 | `task_history`는 최종 상태만 남아 "어느 이벤트가 들어왔는가"를 증명할 수 없다. 이벤트 단위 영수증이 있어야 차집합이 정확하다 |

---

## 1. Simulator 현재 상태와 M3 이벤트 범위

### 점검 결과

| 구성 | 현재 | M3에 필요한 것 |
|---|---|---|
| `SimulationLoop` | 로봇 1대 · 작업 1건(M1). 도착하면 종료 | 로봇 N대 · 작업 연속 생성 루프가 없다 |
| `RobotStepper.StepAllAsync` | 다대 로봇 · 틱당 `DecideAsync` 1회 · 거부 시 `Waiting` | 그대로 쓴다 |
| 작업 생애주기 | `RobotStepper`는 경로 1개 = 작업 1개. 도착 시 `Idle` + `Completed`. 적재 · 하역은 `tools/TraceExport`에만(시연용 체류) | 03의 `Idle → Moving → Loading → Moving → Unloading → Idle` 이 Simulator 본체에 없다 |
| 작업 생성기 · 할당기 | 없음 | 09 결정: 최소 생성기 포함 |
| 이벤트 | 없음. `ConsoleRenderer` 로그만 | 수집기(`IEventSink`) |

### M3에서 추가하는 것 (M2 잔여 — 교착 — 는 손대지 않는다)

| 구성 | 위치 | 동작 |
|---|---|---|
| `FleetSimulation` | `src/Simulator/Fleet/` | 로봇 N대 루프. 틱마다 ① 생성 ② 할당 ③ `StepAllAsync` ④ 생애주기 전이 ⑤ 이벤트 확정 ⑥ 송신 큐에 넣기 |
| `TaskGenerator` | 〃 | 포아송(λ건/초, 틱당 `λ × tickMs/1000` 기대값), 출발 · 목적은 **`task_pattern_flows`의 `weight`에 따라 설비 쌍 추첨**(01 기준정보, 출발 ≠ 목적은 DB 제약). **시드 고정 가능**(재현 실험). 현재 구현은 Station 무작위 쌍 — 전환은 후속 작업 |
| `TaskDispatcher` | 〃 | 대기 작업 FIFO × 맨해튼 거리 최근접 `Idle` 로봇. 동점은 `robotId` 오름차순 |
| `TaskLifecycle` | 〃 | 작업 1건 = 이동 구간 2개(→ 출발지, → 목적지). `RobotStepper`에는 구간마다 내부 `TransportTask`를 넘기고(TraceExport 방식), 도메인 작업 상태는 여기서 관리. 적재 · 하역 체류 = `WorkMs`(기본 2000ms, 03 예시 `workMs 4000`의 절반씩) |
| `EventCollector` | `src/Simulator/Events/` | `IEventSink` 구현. 틱 안의 발생을 받아 03 순서로 배치 1건 확정 |

| 결정 | 근거 |
|---|---|
| M1 `SimulationLoop`과 명령행은 그대로, `FleetSimulation`은 별도 진입(`--robots N`) | M1 테스트(T-01~T-13) 회귀 없음 |
| `RobotStepper` 수정은 **관찰 지점 추가만**(거부 결정 · 상태 전이 콜백) | 판정 경로(M2 · M4)와 충돌을 최소화 |
| 배터리 · 충전 작업은 M3 범위 밖 | 생애주기 증명에 필요 없다. `battery`는 03 소모율로 값만 갱신 |

### 상태 전이 · 사유 (M3에서 실제로 나는 것)

| from → to | 사유 | 발생 지점 |
|---|---|---|
| Idle → Moving | `task_assigned` | 할당 직후 |
| Moving → Waiting | `conflict_wait` | `DecideAsync` 거부 |
| Waiting → Moving | `path_cleared` | 거부 뒤 승인 |
| Moving → Loading | `arrived_origin` | 1구간 도착 |
| Loading → Moving | `load_done` | 체류 종료 |
| Moving → Unloading | `arrived_destination` | 2구간 도착 |
| Unloading → Idle | `unload_done` | 체류 종료 |
| Moving → Error | `no_path` | 경로 없음 |

할당 시 이미 출발지에 서 있으면 같은 틱에 `Idle → Moving → Loading` 두 건을 낸다(03이 `Idle → Loading`을 금지).
전이 표는 `src/Shared/Domain/StatusTransitions.cs` 하나에 두고 Simulator(발행 전 단언)와 워커(검증)가 같이 쓴다.

### 이벤트 발생 규칙

| 이벤트 | 시점 | 비고 |
|---|---|---|
| TaskCreated | 생성기 | |
| TaskAssigned | 할당기 | `plannedPathLength` = 1구간 + 2구간 A* 길이, `estimatedDurationMs` = 칸 수 × `MoveMsPerCell` + 회전 + `WorkMs × 2` |
| TaskStarted | `arrived_origin` | 적재 시작 = 작업 시작 |
| TaskCompleted | `unload_done` | `actualSteps` · `moveMs` · `waitMs` · `workMs` · `replanCount(0)` |
| TaskFailed | `no_path` | `reason: no_path` |
| ConflictDetected | **대기 구간이 끝날 때 1건**(`path_cleared` · `Error`) + 종료 시 열린 구간 | `waitMs` = 구간 길이. 매 거부마다 내면 대기 로봇 1대가 초당 10건을 만들어 05 충돌 발생률이 대기 시간에 비례해 부풀려진다. `conflictType` = `occupied → vertex`, `swap → edge` |
| RobotStatusChanged | 위 표 | |
| RobotTelemetry | 매 틱 전 로봇 | 03 |

### 배치 안 순서 (03 "틱 안의 발생 순서")

| 순 | 내용 |
|---|---|
| 1 | TaskCreated (생성 순) |
| 2 | 로봇별(`robotId` 오름차순)로: ConflictDetected → RobotStatusChanged(발생 순) → Task* |
| 3 | RobotTelemetry (`robotId` 오름차순) |

같은 로봇의 `RobotStatusChanged`가 그 로봇의 telemetry보다 앞선다(03 규칙). 배치가 1000건을 넘으면 같은 순서를 유지한 채 잘라
같은 tick의 두 번째 배치(`batchId` 별도, UUIDv7이라 첫 배치보다 큼)로 보낸다.

---

## 2. `Shared.Messaging` 계약

`src/Shared/Messaging/` (프로젝트 `RobotFleet.Shared`, 외부 패키지 없음).

### 봉투

```json
{
  "schemaVersion": 1,
  "batchId": "018f3c2a-7b1e-7c3d-9a2f-0123456789ab",
  "mapId": "factory-a",
  "runId": "20261013-143022-r200",
  "tick": 142057,
  "tickMs": 100,
  "occurredAt": "2026-04-01T09:14:23.412Z",
  "events": [ { "eventType": "RobotTelemetry", "payload": { } } ]
}
```

큐 메시지 본문 = 항목 하나의 공통 봉투(03)에 `schemaVersion`을 더한 것. `eventId`는 Ingest가 `{batchId}/{index}`로 채우고,
`tickMs` · `runId`는 배치 봉투 값을 항목마다 복사한다(03). 워커는 telemetry 1행을 이 값만큼의 상태 시간으로 환산하고,
`runId`로 모든 행의 run을 가른다. `runId`가 비면 Ingest는 400, 워커는 독 메시지로 처리한다.

| 항목 | 결정 | 근거 |
|---|---|---|
| 타입 | `EventBatch`, `EventEnvelope`(둘 다 `MapId` 다음에 `RunId`), 이벤트별 `sealed record` 페이로드 12종(03 이벤트 10종 + `SimulationStarted` · `SimulationEnded`), `EventType` 상수, `EventCategory { Telemetry, Task, Conflict }` | 03 스키마 그대로 |
| 다형성 | `eventType` 문자열로 판별, 페이로드는 `JsonElement`로 받아 타입별 역직렬화 | `$type` 같은 라이브러리 판별자를 넣지 않는다 — 03 JSON 그대로 |
| 직렬화 | `System.Text.Json` **소스 생성기**(`FleetJsonContext`), camelCase, 숫자 문자열 금지, `null` 생략 | 리플렉션 없음 · 빠름. 06 원칙(직렬화 가능한 것만) |
| 필드 순서 | 선언 순서 고정 + `[JsonPropertyOrder]` | 송신 로그 · 해시 · 대조가 바이트 단위로 같아야 한다 |
| `schemaVersion` | 정수 1. Ingest는 모르는 버전 → 400, 워커는 → `fleet.invalid` | 바뀌는 날 양쪽이 같이 판단한다 |
| 라우팅 키 | `RoutingKeys.Of(envelope)` 순수 함수 — telemetry → `robotId`, Task* → `taskId`, RobotStatusChanged → `robotId`, conflict 범주 → `mapId` | 01 표. 한 곳에서만 정한다 |
| 범주 · 지속성 | `EventCategories.Of(eventType)` — 03 "이벤트 → 큐 매핑" 표 그대로. PathReplanned는 conflict 범주(08 §8) | |
| 메시지 속성 | `MessageId = eventId`, `Type = eventType`, `ContentType = application/json`, `DeliveryMode` = 범주별, 헤더 `x-schema-version` | 워커가 본문을 열기 전에 분기 가능 |
| 토폴로지 명세 | `TopologySpec` — exchange · 큐 · 인자 · 바인딩을 **데이터로** 정의. 선언 실행은 인프라 프로젝트 | Shared를 외부 패키지 없이 유지(09 "Shared.Messaging에 토폴로지 선언"을 데이터로 해석) |
| 인터페이스 6종 | `IEventSink` · `IEventPublisher` · `IIdemStore` · `IEventStore` · `IPartitionRoster` · `IPartitionSubscriber` 를 `src/Shared/Pipeline/` 에 | 인프라 · 서비스가 순환 없이 참조 |

---

## 3. 메시징 라이브러리 — RabbitMQ.Client 7

### 01 토폴로지 항목별

| # | 01 · 08 요구 | MassTransit 8 | RabbitMQ.Client 7 |
|---|---|---|---|
| 1 | consistent-hash exchange, 라우팅 키 = `robotId`/`taskId`/`mapId` | △ exchange 타입 지정 · 라우팅 키 포매터 가능. 단 메시지 타입별 exchange를 자동으로 만드는 publish 토폴로지를 꺼야 한다 | ○ `ExchangeDeclareAsync(type: "x-consistent-hash")` |
| 2 | 03 봉투 그대로(`eventId` = `MessageId`) | △ 기본은 자체 봉투(`messageType` URN 등). raw JSON 직렬화기로 바꿔야 한다 | ○ 바이트를 그대로 보낸다 |
| 3 | quorum 인자 · SAC · `x-delivery-limit` · DLX at-least-once · `reject-publish` | ○ `SetQuorumQueue` · `SingleActiveConsumer` · `SetQueueArgument` | ○ 인자 사전 그대로 |
| 4 | 명부 기반 재배정 — 새 몫 구독 → 옛 몫 취소, 순서 보장 | △ `ConnectReceiveEndpoint` · `StopAsync` 동적 연결 가능. 취소 시 미확인 메시지 반환 시점을 제어하기 어렵다 | ○ `BasicConsumeAsync` / `BasicCancelAsync` / 채널 닫기 순서를 직접 쓴다 |
| 5 | 파티션 단위 배치, **처리 완료 순서대로 ack** | ✗ Batch 소비자는 묶음 단위 완료 처리이고 동시성 · 재시도 파이프라인을 모두 1로 묶어야 순서가 선다. 오류 시 메시지를 `_error` 큐로 **재발행 후 ack** 한다 → `x-delivery-limit` · DLX가 동작하지 않는다 | ○ `BasicAckAsync(lastTag, multiple: true)` · `BasicRejectAsync(requeue: false)` |
| 6 | DB 일시 장애는 nack하지 않고 같은 배치 재시도(전달 횟수 보존, 08 §8) | ✗ 재시도 · 재전달 정책이 메시지를 다시 넣거나 오류 큐로 보낸다. 끄려면 필터를 갈아 끼워야 한다 | ○ ack하지 않고 기다리면 된다 |
| 7 | publisher confirm, 범주별 타임아웃(2s / 200ms), telemetry만 drop | △ 확인은 기본. 범주별 타임아웃 · 부분 실패 판정은 직접 | ○ 확인 추적을 직접(§4) |

> **결정: RabbitMQ.Client 7.** ✗ 두 개(5 · 6)가 08 §8의 유실 0 · 독 메시지 장치를 무력화하고, △ 네 개는 기본 동작을 끄는 작업이다.
> 토폴로지가 라이브러리보다 우선(09). README 스택 표를 고친다(§14).
> 부수 사실: MassTransit은 v9부터 상용 라이선스로 바뀌었다(v8은 오픈소스). 결정의 근거는 아니다.

| 항목 | 값 | 근거 |
|---|---|---|
| 패키지 | `RabbitMQ.Client` 7.1.x | async 전용 API(`IChannel`), 채널별 확인 |
| 연결 | 서비스당 연결 1개, 발행 채널 2개(신뢰 · telemetry) / 파티션당 소비 채널 1개 | 채널은 스레드 안전하지 않다. 소비 채널을 파티션마다 두면 `multiple: true` ack가 그 파티션에만 걸린다 |
| 자동 복구 | `AutomaticRecoveryEnabled = true`, `TopologyRecoveryEnabled = false` | 토폴로지 재선언은 우리가 한다(§3 선언). 소비자 복구는 배정 코디네이터가 다시 건다 |
| heartbeat | 10s | 08 §8 |

### 토폴로지 선언

| 항목 | 결정 | 근거 |
|---|---|---|
| 주체 | IngestApi · EventWorker **둘 다** 기동 시 · 재연결 시 | 어느 쪽이 먼저 떠도 된다. 선언은 같은 인자면 멱등 |
| 순서 | exchange 4(`fleet.telemetry` · `fleet.task` · `fleet.conflict` · `fleet.dlx`) → 큐 50(파티션 48 + `fleet.dlq` + `fleet.invalid`) → 바인딩 49(파티션 48은 가중치 `"1"`, `fleet.dlx` → `fleet.dlq` 1개) | 바인딩 전에 양끝이 있어야 한다 |
| 인자 | 08 §8 표 그대로. M3는 단일 노드라 `x-quorum-initial-group-size` 생략(=1) | 08 |
| `PRECONDITION_FAILED`(기존 큐와 인자 다름) | **기동 실패**, 로그에 큐 이름 · 차이 | 자동 삭제는 메시지를 지운다. 사람이 판단한다 |
| consistent-hash 플러그인 없음(`COMMAND_INVALID`/unknown exchange type) | 기동 실패, "rabbitmq_consistent_hash_exchange 활성화 필요" | 원격 서버 준비 확인(09 사용자 확인) |
| 접두사 | `TopologyOptions.Prefix`(기본 빈 값) — 모든 이름 앞에 붙는다 | L2 테스트 자원 격리(§12) |
| readiness | 선언 성공 전 Not Ready | 선언 전 발행은 라우팅되지 않는다 |

---

## 4. IngestApi — 검증 · 멱등 · 발행 · 응답

`POST /api/v1/events/batch` 한 개. 단건 엔드포인트는 M3에서 만들지 않는다(01 "배치 반드시").

### 처리 순서

| 단계 | 동작 | 실패 시 |
|---|---|---|
| 1 | 스키마 검증: `schemaVersion == 1`, `batchId` UUIDv7, `mapId` · `runId` 비어 있지 않음, `tickMs ≥ 1`, `events` 1~1000, `eventType` 알려진 값, 페이로드 필수 필드 · 라우팅 키 필드 존재 | **400** + 첫 오류 위치. 재시도하지 않을 오류 |
| 2 | `IIdemStore.TryBegin(batchId)` — 08 §9 상태 전이 표 그대로 | `done` → 202(중복), 다른 인스턴스 `pending` 5s 미만 → 503 `Retry-After: 1`, Redis 오류 → 건너뜀 |
| 3 | 메시지 생성: `eventId = {batchId}/{index}`, 라우팅 키, 범주 | — |
| 4 | 발행: **신뢰 채널**(task · conflict)과 **telemetry 채널**에 배열 순서대로 | §아래 |
| 5 | task · conflict 확인 전부 ack → `Complete(batchId)` → 202. 하나라도 nack · 타임아웃 → `Abort(batchId)` → 503 | telemetry 결과는 판정에 넣지 않는다 |

응답 202 본문: `{ "batchId", "accepted": n, "telemetryDropped": k, "duplicate": bool }`.

### 발행 확인 추적

| 결정 | 근거 |
|---|---|
| 확인 모드 켜고 **추적은 직접**: 보내기 전 `GetNextPublishSequenceNumberAsync`로 번호 기록 → `BasicAcks` · `BasicNacks` 이벤트(`multiple` 포함)로 완료 | 메시지마다 확인을 기다리면 1000건 × 왕복이 된다. 보내기는 배열 순서대로 직렬, 확인은 몰아서 받는다 — 순서와 처리량을 같이 얻는다 |
| 채널 1개를 한 배치가 독점(채널 풀, 범주별 1~4개 — **M3 측정**) | 시퀀스 번호가 채널 단위다. 동시 배치가 섞이면 부분 실패 판정이 어긋난다 |
| 타임아웃 task · conflict **2000ms**, telemetry **200ms** | 08 §9 |
| 채널 종료 · 연결 끊김 = 미확인 전부 실패 | 확인을 못 받은 것은 들어갔는지 모른다 → 503 → 재전송(중복은 워커가 막는다) |
| `mandatory = false` | consistent-hash exchange는 바인딩이 있으면 항상 라우팅한다. 바인딩은 선언 단계가 보장 |
| `reject-publish` 초과 · 메모리 경보로 인한 nack | 일반 nack과 같다 → 503 → Simulator 순차 재전송이 역압 |

### Redis 멱등 Lua

`TryBegin` — `SET NX`에 실패하면 값을 읽고, `pending:{t}`이면서 `now − t ≥ 5000`이면 원자적으로 갈아끼운다.

```lua
-- KEYS[1] = idem:{batchId}   ARGV[1] = nowMs   ARGV[2] = ttlSec(600)   ARGV[3] = orphanMs(5000)
local ok = redis.call('SET', KEYS[1], 'pending:' .. ARGV[1], 'NX', 'EX', ARGV[2])
if ok then return {'BEGIN', ARGV[1]} end
local v = redis.call('GET', KEYS[1])
if not v then                                   -- 사이에 만료
  redis.call('SET', KEYS[1], 'pending:' .. ARGV[1], 'EX', ARGV[2])
  return {'BEGIN', ARGV[1]}
end
if v == 'done' then return {'DONE'} end
local t = tonumber(string.sub(v, 9))
if tonumber(ARGV[1]) - t >= tonumber(ARGV[3]) then
  redis.call('SET', KEYS[1], 'pending:' .. ARGV[1], 'EX', ARGV[2])
  return {'TAKEOVER', ARGV[1]}
end
return {'BUSY'}
```

`Abort` — 내가 쓴 `pending:{myT}`일 때만 지운다(01). `Complete` — `SET … "done" XX EX 600`.

| 설정 | 값 | 근거 |
|---|---|---|
| Redis 명령 타임아웃 | 200ms | 넘으면 fail-open. Ingest 응답을 Redis가 붙잡지 않는다 |
| 연결 실패 후 재시도 간격 | 1s(백그라운드) | 실패 동안 매 배치가 연결 시도로 느려지지 않게. 그동안 전부 fail-open |
| 메트릭 | `fleet_ingest_idem_bypass_total`, `fleet_ingest_idem_orphan_takeover_total` | 08 |

---

## 5. Simulator 송신기

```
틱 루프 ──TryWrite──► Channel<EventBatch>(600) ──► 송신 루프(백그라운드) ──IIngestClient──► IngestApi
   │ 가득 → WaitToWriteAsync (틱 정지)                 │ 202 → 송신 로그 기록 → 다음
                                                         │ 503 · 타임아웃 · 연결 실패 → 같은 배치 재시도
                                                         │ 400 → 송신 정지 · 시뮬레이션 오류 종료
```

| 항목 | 값 | 근거 |
|---|---|---|
| 버퍼 | `Channel.CreateBounded(600, FullMode = Wait)`, 단일 소비자 | 08 §9: 60초분. 200대 배치 약 60KB × 600 = 36MB |
| 틱 루프 쪽 | `TryWrite`(O(1)). 실패하면 `WaitToWriteAsync`로 **틱 정지**, `fleet_tick_stall_seconds{reason="event_buffer"}` | 이벤트 유실보다 정지가 낫다(07). 틱 루프는 직렬화 · 네트워크를 하지 않는다 |
| 직렬화 위치 | 송신 루프 | 틱 예산 보호(09 완료 기준 5) |
| 순서 | 머리 배치 하나만 보낸다. 202를 받아야 다음 | 08 §9 순차 |
| 재시도 | 같은 바이트, 같은 `batchId`. 백오프 100ms → ×2 → 최대 2000ms | Ingest 재시작 · 브로커 리더 재선출(수 초)을 버틴다 |
| 시도 타임아웃 | 5000ms | Ingest 확인 대기 2초 + 여유 |
| 400 | 송신 정지 → 시뮬레이션 종료 코드 4, 배치 내용을 오류 파일에 덤프 | 검증 실패는 버그다. 재시도해도 같다. 조용히 버리면 유실 |
| 종료 | 취소 신호 후 버퍼를 비울 때까지 최대 30초 대기, 남은 수를 `unsent`로 보고 | 완료 기준 1은 `unsent = 0`인 실행만 인정 |
| HTTP | `SocketsHttpHandler`, `PooledConnectionLifetime = 2min` | Ingest pod 교체 · DNS 변경을 따라간다 |

### 송신 로그 (완료 기준 1의 "송신 집합")

| 항목 | 값 |
|---|---|
| 파일 | `artifacts/sendlog/{mapId}-{runId}.jsonl` (`artifacts/`는 이미 `.gitignore`) |
| 기록 시점 | **202를 받은 뒤** 배치 단위로 append + flush |
| 행 | 이벤트당 1행 `{"eventId","runId","batchId","tick","eventType","category","key"}` — `key`는 라우팅 키(telemetry 대조용 `robotId`), `runId`는 배치 봉투 값 |
| run 식별자 | `{yyyyMMdd}-{HHmmss}-r{robots}` (UTC, `RunIds.New`). 파일명과 줄의 `runId`가 같다 |
| 근거 | 202 이후 = Ingest가 책임을 넘겨받은 집합. 202 전 버퍼분은 종료 시 `unsent`로 따로 센다 |

`IIngestClient` 구현은 `HttpIngestClient` 하나다(D7).

---

## 6. EventWorker — 소비 · 배치 · 집계 · 검증 · DLQ

### 파티션 처리기 (파티션 큐 하나당 1개)

| 항목 | 값 | 근거 |
|---|---|---|
| 소비 | 채널 1개 · `BasicQos(prefetch 50)` · SAC 큐 | 01 |
| 배치 창 | **N = 50 또는 T = 50ms** 중 먼저 (M3 측정) | prefetch가 50이라 미확인 상한도 50. 200대 기준 파티션당 125건/초 → 50ms 창에 약 6건. 지연 p99 5초 예산에 여유가 크다 |
| 처리 | 배치 1개 = PG 트랜잭션 1개 | 개별 INSERT 금지(01) |
| ack | 커밋 성공 후 배치의 마지막 tag로 `multiple: true` | 처리 완료 순서대로, 건너뛰기 없음 |
| DB 일시 오류 | ack · nack 하지 않고 같은 배치를 백오프(200ms → ×2 → 5s) 재시도. 그동안 그 파티션 소비가 멈춘다(prefetch 소진) | 전달 횟수를 올리지 않는다(08 §8). PostgreSQL 다운 동안 큐가 버퍼(07) |
| 역직렬화 실패 · 모르는 `schemaVersion` | 그 메시지만 `BasicReject(requeue: false)` → DLX → `fleet.dlq` | 독 메시지. 같은 배치 나머지는 계속 |
| 상태 전이 위반 | `robot_status_log`에 **`valid = false`로 영수증을 남기고**, `robot_state`는 갱신하지 않는다. `fleet.invalid`로 발행(확인 대기) 후 ack | 03. 영수증을 남겨야 Reconcile 차집합(기준 1 · 유실)과 위반 수(기준 3 · 순서)가 섞이지 않는다 |
| (통지의 보장 수준) | `fleet.invalid` 통지는 커밋 뒤에 하므로 발행 실패 후 재시도하면 누락될 수 있다. **통지는 best-effort, 진실은 `robot_status_log.valid`** | 위반 집계 · 조사는 통지가 아니라 영수증 기준 |
| 프로세스가 죽음 | 미확인분은 브로커가 반환 → 다음 활성 소비자가 재처리 → 중복은 테이블이 막는다 | 반복 크래시는 `x-delivery-limit 20` → DLQ |

### 이벤트별 저장

| 이벤트 | 테이블 · 방식 | 멱등 |
|---|---|---|
| RobotTelemetry | `robot_telemetry` `unnest` INSERT `ON CONFLICT DO NOTHING RETURNING robot_id, status, occurred_at` | 자연 키 `(run_id, robot_id, tick, occurred_at)`(01) |
| (집계) | 위 RETURNING 행만 `metrics_minute`에 상태별로 그 행의 봉투 `tickMs` 가산 (Moving → move, Loading · Unloading → work, Waiting → wait, Idle → idle) | **새로 들어간 행만** 집계(D4) |
| Task* | `task_event_log` INSERT `ON CONFLICT (event_id) DO NOTHING RETURNING` → 새 행만 `task_history` 단조 upsert(01) | 이벤트 영수증 + 단조 상태 |
| (집계) | 새로 들어간 TaskCompleted만 `metrics_minute.task_count + 1` | 위와 같은 방식 |
| RobotStatusChanged | 검증 → `robot_status_log` INSERT(`valid` 포함) `ON CONFLICT DO NOTHING RETURNING` → 새 행이면서 `valid`인 것만 `robot_state` 갱신 | 위반도 영수증은 남는다 |
| ConflictDetected · DeadlockDetected | `conflict_log` `ON CONFLICT (event_id) DO NOTHING`. DeadlockDetected는 **`resolved_ms` · `yield_robot_id` · `yield_to_x/y` · `searched_distance`**도 적재. **양보가 없으면(해소 실패 등) `yield_to_x/y`는 NULL** — 페이로드의 0은 좌표가 아니라 "없음"이다 | 01 |
| PathReplanned | **`replan_log` INSERT** `ON CONFLICT (event_id) DO NOTHING` | 01. 05 재계획률 · 실험 5 · M9 `replan_storm`의 입력. "M7에서 정한다"를 M3로 당겼다 — M7이 바로 이 지표를 쓰는 실험이다 |
| SimulationStarted | `simulations` **UPSERT** (`ON CONFLICT (run_id) DO UPDATE`로 메타 · `line_code` · `pattern_code` · `map_code` · `map_version` · `map_hash` · `params` 채움). **`mapHash`를 DB `maps`와 대조**, 다르면 경고 로그 | `run_id` |
| SimulationEnded | 같은 행에 종료 통계 UPDATE | `run_id` |
| (run 집계) | **Reconcile 차집합 0 확인 뒤** `run_metrics` 1행 생성 | `SimulationEnded` 직후에는 다른 파티션의 telemetry가 아직 적재 중일 수 있다. Reconcile이 이미 "적재 완료"를 판정하므로 그 결과를 재사용한다 |

**모든 INSERT에 봉투의 `runId`를 싣고, 봉투의 `mapId`는 `map_code` 컬럼에 넣는다**(01 키 정책).

> **`simulations`로 FK를 걸지 않는다.** `SimulationStarted`는 `mapId` 라우팅, telemetry는 `robotId` 해시 라우팅이라
> **다른 파티션**이고 순서가 보장되지 않는다. telemetry가 먼저 도착하면 FK 위반 → 반환 → DLQ가 된다.
> 사실 테이블 적재 시 `simulations` 행이 없으면 `run_id`만 가진 **최소 행을 UPSERT**로 만들고,
> `SimulationStarted`가 나중에 와서 나머지를 채운다.

`tickMs`는 메시지 봉투에서 읽는다(§2). 워커에 틱 길이 설정이 없으므로 Simulator `--tick`을 바꿔도 시간 분해가 맞는다.

### 배치 안 eventId 중복 제거 (적재 직전)

`HandleAsync` 맨 앞에서 `BatchDedup.ByEventId`로 같은 `eventId`(= `Delivery.MessageId`)를 처음 1건만 남긴다.

| 항목 | 결정 | 근거 |
|---|---|---|
| 왜 생기나 | Ingest 2단계 멱등의 "발행 후 확정 실패 → 재송신" 구간에서 같은 `eventId`가 한 배치에 두 번 들어올 수 있다 | 재송신은 같은 `batchId` · 같은 바이트. 두 전달이 같은 파티션 배치에 겹치면 중복 |
| 안 막으면 | `PgEventStore`가 `TaskEvents.ToDictionary(e => e.EventId)`에서 `ArgumentException` → 같은 배치 무한 재시도 → **파티션 정지**. 상태 이벤트는 두 번째 사본이 `robot_state` 기준과 어긋나 `invalid`로 **잘못 통지** | 적재 전에 걸러야 둘 다 안 난다 |
| 판정 키 | `MessageId`(발행자가 `eventId`로 채운다, §2). 역직렬화 없이 순수 | 빈 `MessageId`는 중복으로 묶지 않는다 — 깨진 메시지는 역직렬화 단계가 개별로 독 메시지 처리 |
| 걸러낸 delivery | poison이 아니라 나머지와 함께 `multiple` ack. 이미 저장된 사본의 중복이므로 | DLQ 대상이 아니다 |
| 집계 | `fleet_worker_batch_dedup_total` + `BatchOutcome.Duplicate`에 가산 | 중복 시도 수(완료 기준 2)에 함께 보인다 |

### 저장 오류 분류 · 결정적 오류 격리

지금까지 `StoreBatchAsync`의 모든 예외는 "일시 오류"로 보고 같은 배치를 무한 재시도했다. 그래서 **데이터 · 제약 · 문법 오류 하나로 파티션이 영구 정지**한다. 오류를 둘로 가른다.

| 분류 | SQLSTATE | 처리 | 근거 |
|---|---|---|---|
| 결정적 | `22`(데이터) · `23`(제약) · `42`(문법 · 접근) | 재시도 안 함. 원인 메시지를 DLQ로, 경보 로그(Error) · `fleet_worker_deterministic_dlq_total` | 몇 번 보내도 같다. 파티션을 세우느니 그 메시지만 떼어 낸다 |
| 일시적 | `08`(연결) · `53`(자원) · `57`(개입 · 취소) · 그 밖(타임아웃 · 소켓) · SQLSTATE 없음 | 기존대로 ack · nack 없이 백오프(200ms → ×2 → 5s) 무한 재시도 | 재시도하면 풀린다. 전달 횟수 보존(08 §8) |

분류는 `DbErrorClass.IsDeterministicSqlState`(순수, Shared) + `PgEventStore`가 결정적 `PostgresException`을 `DeterministicStoreException`으로 감싸 올린다. `PostgresException` 의존은 저장소 구현에만 둔다.

**어느 메시지가 원인인지 모를 때 — 배치 분할 격리.** 결정적 예외가 오면 `PartitionProcessor`가 배치를 절반씩 나눠 재처리하고, 1건까지 좁혀진 하위 배치가 또 결정적으로 실패하면 **그 1건만 DLQ**로 보내고 나머지는 정상 적재한다.

| 항목 | 결정 | 근거 |
|---|---|---|
| 분할 재처리 안전성 | 결정적 예외는 `CommitAsync` 전에 던져지고 트랜잭션은 롤백돼 있다 — 하위 배치는 아무것도 커밋되지 않은 상태에서 다시 시작한다. 재처리 자체도 `ON CONFLICT`로 멱등 | 부분 커밋 없음 |
| 전이 검증 의미 | 분할은 `robot_state` 기준을 하위 배치 단위로 다시 본다. 드문 복구 경로라 허용 | 결정적 오류는 이미 데이터가 깨진 상황 |
| `42` 계열(스키마 · 코드 버그) | 모든 메시지에 걸려 분할이 1건씩 전부 DLQ로 보낸다 | 무한 정지보다 낫다. 경보 · DLQ로 운영자가 고친 뒤 DLQ 재생 |
| ack | 격리한 1건은 `poison`으로 개별 reject(DLX → DLQ), 나머지는 `multiple` ack | 기존 ack 경로 그대로 |

> 일시 오류(PostgreSQL 다운 등)는 **여전히 무한 재시도**다 — 유실 0 · 전달 횟수 보존 불변식을 건드리지 않는다. 바뀐 것은 "결정적 오류는 재시도 대상이 아니다"뿐이다.

### 상태 전이 검증

| 항목 | 결정 | 근거 |
|---|---|---|
| 기준 상태 | `robot_state(run_id, robot_id, map_code, status, last_tick, last_event_id)`, **PK `(run_id, robot_id)`**. 조회도 `(run_id, robot_id)` | 재배정으로 파티션 주인이 바뀌어도 이어서 검증. **`run_id`가 빠지면 run 2의 첫 전이 `Idle → Moving`이 이전 run의 `Moving`과 불일치 판정되어 로봇 전부가 `valid=false` 영수증 + `fleet.invalid`가 된다** — M3 완료 기준 "invalid 0"이 무너진다 |
| 규칙 | `(from, to, reason)`이 `StatusTransitions` 표에 있고, `from == robot_state.status`(행이 없으면 첫 관측으로 통과) | 03 |
| 중복 전달 | `robot_status_log`에 이미 있는 `event_id`면 검증 없이 건너뜀 | 크래시 후 재전달이 "from 불일치"로 오탐되지 않게 |

---

## 7. 명부 기반 파티션 배정

01 "워커 수가 변할 때" 그대로. 배정 단위는 **범주별 파티션 번호**: 워커 하나가 `p % N == rank`인 번호의 telemetry · task · conflict 큐 3개를 함께 가진다.

| 항목 | 값 | 근거 |
|---|---|---|
| 명부 | `worker:members` Sorted Set, 점수 = 마지막 heartbeat ms | 01 |
| heartbeat · 만료 | 10s · 30s. 각 워커가 heartbeat 때 `ZREMRANGEBYSCORE -inf (now−30s)` | 01 |
| `workerId` | `{hostname}-{기동 unixMs}` | 같은 pod 재시작도 새 멤버 |
| 순위 | 살아 있는 멤버를 **id 문자열 순**으로 정렬한 위치 | 점수(heartbeat)로 정렬하면 heartbeat마다 순위가 바뀐다 |
| 재계산 주기 | 5s, 같은 결과가 **2회 연속**일 때만 적용 | 기동 직후 heartbeat 엇갈림으로 몫이 출렁이지 않게 |
| 전환 순서 | ① 새 몫 `BasicConsume`(SAC라 대기로 붙음) ② 옛 몫: 진행 중 배치 커밋 · ack → `BasicCancel` → **채널 닫기** | 취소만으로는 미확인분이 반환되지 않는다. 채널을 닫아야 반환된다. 반환이 전달 횟수를 올리는지는 버전마다 다르다(3.13은 올리는 쪽) — 올려도 한도 20이 재배정 · 크래시 수 회를 견딘다. C-04로 확인 |
| Redis 미설정 | 단일 워커, 48큐 전부 | 09 |
| Redis 설정됐으나 기동 시 불통 | 48큐 전부 구독. SAC가 동시 소비를 막는다 | 기동을 Redis에 묶지 않는다. 분배만 나빠진다 |
| 실행 중 Redis 끊김 | **현재 몫 고정**, 복구 후 재계산 | 끊긴 동안 모두가 "나 혼자"로 계산하면 전원이 전부를 잡으려 든다 |

---

## 8. PostgreSQL

### 마이그레이션

| 항목 | 결정 | 근거 |
|---|---|---|
| 파일 | `db/migrations/001_schema.sql`, `002_logs_and_state.sql`, `003_partition_fn.sql` … | 09 결정 |
| 적용 주체 · 시점 | EventWorker 기동 시, 소비 시작 **전** | 처음 띄울 때 테이블 없이 메시지를 받지 않게 |
| 동시 기동 | `pg_advisory_lock(hashtext('fleet:migrations'))` 안에서 | 워커 2대가 같이 떠도 한 번만 적용 |
| 이력 | `schema_migrations(version text PK, checksum text, applied_at)` | |
| 파일별 트랜잭션 | 실패 시 롤백 · 기동 실패 | 반쯤 적용된 스키마 없음 |
| 체크섬 불일치(적용된 파일이 바뀜) | 기동 실패 | 적용된 마이그레이션은 고치지 않고 새 번호로 |

### 스키마 (01 테이블 그대로 + 추가분)

| 테이블 | 키 | 추가 이유 |
|---|---|---|
| `robot_telemetry` (일 파티션) + `robot_telemetry_default` | `(run_id, robot_id, tick, occurred_at)` | DEFAULT는 D3 |
| `simulations` · `task_history` · `conflict_log` · `replan_log` · `metrics_minute` · `run_metrics`, 기준정보 7개 | 01 | |
| `task_event_log(event_id PK, run_id, map_code, task_id, event_type, tick, occurred_at)` | `event_id` (유지) + `run_id` 인덱스 | D6 |
| `robot_status_log(event_id PK, run_id, map_code, robot_id, tick, from_status, to_status, reason, valid, occurred_at)` | `event_id` (유지) + `run_id` 인덱스 | D6 · 중복 판별. `valid = false`는 전이 위반 |
| `robot_state(run_id, robot_id, map_code, status, last_tick, last_event_id)` | **`(run_id, robot_id)`** | §6 검증 |
| `schema_migrations` | `version` | |

> 영수증 두 테이블은 PK를 건드리지 않는다 — `eventId = {batchId}/{index}`이고 batchId가 UUIDv7이라 run 간 충돌이 없다.

### 파티션

| 항목 | 결정 | 근거 |
|---|---|---|
| 함수 | `fleet_ensure_telemetry_partition(day date)` — `CREATE TABLE IF NOT EXISTS robot_telemetry_YYYYMMDD PARTITION OF …` | 멱등 |
| 호출 | 마이그레이션 직후(당일 · 익일, **UTC**) + 매시 정각 | D3. `occurred_at`이 UTC라 날짜 경계도 UTC |
| DEFAULT에 행이 생김 | 메트릭 `fleet_telemetry_default_rows`(매시 `count(*)`), 0이 아니면 경보 | 시계 오차 신호. 그 날짜 파티션 생성이 DEFAULT 행과 겹쳐 실패할 수 있으므로 사람이 옮긴다 |

### 배치 INSERT

```sql
INSERT INTO robot_telemetry (run_id, map_code, robot_id, tick, x, y, heading, status, battery, occurred_at)
SELECT * FROM unnest(@run_id::text[], @map_code::text[], @robot_id::text[], @tick::bigint[], @x::smallint[], @y::smallint[],
                     @heading::smallint[], @status::smallint[], @battery::smallint[], @occurred_at::timestamptz[])
ON CONFLICT DO NOTHING
RETURNING robot_id, status, occurred_at;
```

| 설정 | 값 | 근거 |
|---|---|---|
| 방식 | `unnest` 배열 파라미터 | 행 수와 무관하게 파라미터 10개. 다중 VALUES는 65535 파라미터 한계와 매번 다른 SQL 문 |
| Npgsql | 8.x, `NpgsqlDataSource` | |
| `MaxPoolSize` | **10** / 워커 | 워커 최대 3대(HPA, 02) × 10 = 30 < PostgreSQL 기본 `max_connections` 100. 파티션 처리기는 연결 세마포어로 대기 |
| 명령 타임아웃 | 10s | 배치는 수 ms. 넘으면 일시 오류로 보고 재시도 |

---

## 9. 실행 구성

모든 진입점이 같은 조립 함수(`services.AddFleetIngest(config)`, `AddFleetWorker`, `AddFleetSimulatorEvents`)를 쓰고, 구현 선택은 설정으로만 갈린다.

| 모드 | 진입점 | 구성 |
|---|---|---|
| **원격 인프라** | Simulator · IngestApi · EventWorker 각 프로세스 | 실제 RabbitMQ · Redis · PostgreSQL |
| (M5) k3s | 같은 이미지 | 같은 설정 키, ConfigMap · Secret |

| 설정 키 | 의미 | 없을 때 |
|---|---|---|
| `ConnectionStrings:RabbitMq` | AMQP URI | IngestApi · EventWorker 기동 실패 |
| `ConnectionStrings:Redis` | StackExchange.Redis 구성 문자열 | 등록하지 않는다 → `IngestService`는 멱등 1단계 생략, `AssignmentCoordinator`는 단일 워커(대체 구현 없이 `null` 경로) |
| `ConnectionStrings:Postgres` | Npgsql 연결 문자열 | EventWorker 기동 실패 |
| `Fleet:Topology:Prefix` | 이름 접두사 | 빈 값 |

### 인프라 서버 — 사용자 제공

RabbitMQ · Redis · PostgreSQL은 사용자가 준비하고, 저장소는 **요구사항과 접속 설정 키만** 갖는다. 서버 구성 파일은 두지 않는다.

| 서버 | 요구사항 | 근거 |
|---|---|---|
| RabbitMQ | **3.13 이상**, 플러그인 `rabbitmq_consistent_hash_exchange` 활성, quorum queue 사용 가능. 관리 플러그인은 선택(있으면 잔여 자원 청소 · 적체 확인) | 01 파티션 exchange · 08 §8 |
| RabbitMQ 권한 | 사용할 vhost에 configure · write · read (exchange · 큐 선언 · 삭제) | 기동 시 토폴로지 선언(§3), 테스트 접두사 자원 생성 · 삭제(§12) |
| RabbitMQ 권장 설정 | `vm_memory_high_watermark.relative = 0.6`, `raft.wal_max_size_bytes = 64000000`, `disk_free_limit.absolute = 2GB` | 08 §8. 단일 노드에도 해당하는 것만 |
| Redis | **7.2** (단일 노드로 충분). 비밀번호 사용 시 연결 문자열에 | Lua shebang 플래그 · 멱등 키 · 명부. 없어도 동작(fail-open) |
| PostgreSQL | **16** | 선언형 파티션 · `unnest` |
| PostgreSQL 권한 | 대상 DB에 테이블 · 함수 생성(마이그레이션), 테스트용으로 `CREATE SCHEMA` | §8 · §12 스키마 격리 |

### 접속 정보 — appsettings 파일

접속 정보는 사용자가 **`appsettings.Local.json`에 직접 넣는다.** 저장소에는 키 이름만 있다.

| 순서 (뒤가 이김) | 출처 | 커밋 | 내용 |
|---|---|---|---|
| 1 | `appsettings.json` | ○ | `ConnectionStrings`의 세 키를 **빈 문자열**로. 그 밖의 기본값 |
| 2 | `appsettings.{Environment}.json` | `Development`는 ✗(`.gitignore`) | 선택 |
| 3 | `appsettings.Local.json` | **✗** (`.gitignore`) | **실제 접속 정보.** 없으면 건너뜀(optional) |
| 4 | 환경 변수 `ConnectionStrings__RabbitMq` 등 | — | k3s(M5) Secret · CI 덮어쓰기 경로 |
| 5 | 명령행 | — | |

| 결정 | 근거 |
|---|---|
| 대상 호스트: IngestApi · EventWorker · Reconcile. Simulator는 접속 정보가 없고(`--ingest` URL만) 같은 순서로 `Fleet:*` 키만 읽는다 | 모든 진입점이 같은 규칙 |
| `appsettings.Local.json`을 추가한 **뒤 환경 변수 · 명령행을 다시 추가**한다 | `WebApplication.CreateBuilder`는 이미 환경 변수를 넣어 두므로, 그 뒤에 JSON을 붙이면 JSON이 환경 변수를 이긴다. k3s Secret이 무시되는 사고를 막는다 |
| 빈 문자열 = 설정 없음 (`string.IsNullOrWhiteSpace`) | 커밋된 `appsettings.json`의 빈 값이 "설정됨"으로 읽히지 않게. Redis 빈 값 → fail-open(§4 · §7) |
| `appsettings.Local.json`은 있을 때만 출력 폴더로 복사(csproj `Condition="Exists(...)"`) | `dotnet run` · 빌드 산출물 실행 어느 쪽이든 같은 파일을 읽는다 |
| 로그에 연결 문자열을 찍지 않는다. 호스트 · 포트만 | 로그가 저장소 밖으로 나간다 |

Simulator 명령행 추가: `--robots N` · `--lambda λ` · `--seed n` · `--ingest <url>` · `--sendlog <dir>` · `--metrics-port p`. `--ingest`가 없으면 이벤트를 만들지 않는다(M1 동작 유지).

---

## 10. 유실 0 대조 도구

`src/Reconcile` (콘솔, 프로젝트 `RobotFleet.Reconcile`). M8 §12와 공용. `tools/`는 로컬 도구용으로 `.gitignore`에 있어 쓰지 않는다.

| 단계 | 동작 |
|---|---|
| 1 | 적체 0 대기: 실행 스크립트가 `fleet_queue_depth` 합 = 0이 10초 유지될 때까지 기다린 뒤 도구를 부른다 |
| 2 | 송신 로그를 임시 테이블 `sent(event_id, run_id, batch_id, tick, event_type, category, key)`로 `COPY`(Npgsql binary import). `run_id`는 줄의 `runId`, 없으면 파일명 `{mapId}-{runId}.jsonl`에서 읽고 둘 다 없으면 오류 |
| 3 | 차집합 SQL (아래) |
| 4 | task · conflict 누락 0이면 sent에 나온 run마다 `run_metrics` UPSERT(재집계 가능). **`complete = (simulations.ended_at IS NOT NULL)`도 함께 쓴다** |
| 5 | JSON 요약 출력(`runIds`, 범주별 수, `runMetricsComputed`) + 종료 코드: task · conflict 누락 0이면 0, 아니면 1 |

> **미완료 run 구분(`run_metrics.complete`).** 중단 · 크래시로 `SimulationEnded`가 없는 run은 `simulations.ended_at` · `ticks`가 NULL이라
> `throughput_per_hour` · `tick_overruns` · `stall_ms`가 **조용히 NULL**로 집계된다. 그 run의 run_metrics 행을 아예 안 만들면(계산 제외)
> 부분 집계(시간 분해 · 리드타임 등)까지 사라지고 Reconcile이 "그 run은 없다"처럼 보인다. 그래서 **행은 만들되 `complete = false`로 표시**해
> 분석이 완료 run만 골라 비교하게 한다. `complete` 컬럼은 01 `run_metrics` DDL(`001_schema.sql`)과 `ReconcileQueries.RunMetricsSql`에 반영.

```sql
-- task 범주: Task* + RobotStatusChanged + Simulation*(영수증 대신 simulations.started_at · ended_at)
SELECT s.event_id FROM sent s
WHERE s.category = 'task'
  AND NOT EXISTS (SELECT 1 FROM task_event_log t WHERE t.event_id = s.event_id AND t.run_id = s.run_id)
  AND NOT EXISTS (SELECT 1 FROM robot_status_log r WHERE r.event_id = s.event_id AND r.run_id = s.run_id)   -- valid 무관
  AND NOT (s.event_type = 'SimulationStarted' AND EXISTS (SELECT 1 FROM simulations m WHERE m.run_id = s.run_id AND m.started_at IS NOT NULL))
  AND NOT (s.event_type = 'SimulationEnded' AND EXISTS (SELECT 1 FROM simulations m WHERE m.run_id = s.run_id AND m.ended_at IS NOT NULL));
-- 전이 위반 수 (기준 3) — sent에 나온 run만
SELECT count(*) FROM robot_status_log WHERE run_id IN (SELECT DISTINCT run_id FROM sent) AND NOT valid;
-- conflict 범주 (PathReplanned 제외)
SELECT s.event_id FROM sent s
WHERE s.category = 'conflict' AND s.event_type <> 'PathReplanned'
  AND NOT EXISTS (SELECT 1 FROM conflict_log c WHERE c.event_id = s.event_id AND c.run_id = s.run_id);
-- telemetry: 누락 수만
SELECT count(*) FROM sent s
WHERE s.category = 'telemetry'
  AND NOT EXISTS (SELECT 1 FROM robot_telemetry r
                  WHERE r.run_id = s.run_id AND r.robot_id = s.key AND r.tick = s.tick);
```

> **`run_id` 조건이 없으면 거짓 양성이 난다** — `tick`이 run마다 0부터 다시 시작하므로 이전 run의 행을 이번 run의
> 적재로 착각하거나, 반대로 누락으로 센다. 송신 집합은 이미 run별 파일로 갈라져 있다.

| 출력 | 출처 |
|---|---|
| 범주별 송신 수 · 적재 수 · 누락 수(task · conflict는 id 목록 최대 100개) | 위 SQL |
| `unsent` | Simulator 종료 보고 |
| 중복 시도 수 | 워커 메트릭 `fleet_worker_messages_total{outcome="duplicate"}` |
| telemetry drop 수 | Ingest 메트릭 |

---

## 11. 메트릭 · 헬스

| 서비스 | 노출 | 라이브러리 |
|---|---|---|
| IngestApi | `/metrics` · `/health/live` · `/health/ready` | `prometheus-net.AspNetCore` |
| EventWorker | 관리 포트(기본 8081)에 같은 3개 (D5) | 〃 |
| Simulator | `--metrics-port`일 때 `/metrics`만 | `prometheus-net` `KestrelMetricServer` |

| readiness | 조건 | 근거 |
|---|---|---|
| IngestApi | RabbitMQ 연결 열림 + 토폴로지 선언 완료. **Redis는 보지 않는다** | fail-open이므로 Redis는 의존성이 아니다 |
| EventWorker | RabbitMQ + PostgreSQL 연결 + 마이그레이션 완료 | 02 규칙 |
| liveness | 프로세스 응답만 | 02 |

| 메트릭 | 종류 | 주체 | 완료 기준 |
|---|---|---|---|
| `fleet_events_generated_total{category}` | counter | Simulator | 1 |
| `fleet_event_buffer_depth` | gauge | Simulator | 4 |
| `fleet_event_send_retry_total{reason}` | counter | Simulator | |
| `fleet_tick_stall_seconds{reason}` | histogram | Simulator | 5 |
| `fleet_tick_overrun_total{map_id}` | counter | Simulator | 5 |
| `fleet_events_received_total{event_type}` | counter | IngestApi | |
| `fleet_ingest_batches_total{outcome}` (`accepted` / `duplicate` / `unavailable` / `invalid`) | counter | IngestApi | |
| `fleet_ingest_publish_seconds{category}` | histogram | IngestApi | 4 |
| `fleet_telemetry_dropped_total{reason}` | counter | IngestApi | 6 |
| `fleet_ingest_idem_bypass_total` · `…_orphan_takeover_total` | counter | IngestApi | |
| `fleet_worker_messages_total{category, outcome}` (`stored` / `duplicate` / `invalid` / `dead_lettered`) | counter | EventWorker | 2 · 3 |
| `fleet_worker_batch_size` · `fleet_worker_batch_seconds` | histogram | EventWorker | 4 |
| `fleet_event_lag_seconds` (`occurredAt` → 커밋) | histogram | EventWorker | 4 |
| `fleet_worker_partitions_owned` · `fleet_worker_reassign_total` | gauge · counter | EventWorker | 3 |
| `fleet_queue_depth{queue}` · `fleet_dlq_depth` · `fleet_invalid_depth` (15초마다 passive declare) | gauge | EventWorker | 3 · 4 |
| `fleet_worker_db_retry_total` · `fleet_telemetry_default_rows` | counter · gauge | EventWorker | |

---

## 12. 테스트

> **테스트 대역(fake)을 두지 않는다**(D7, 사용자 지시). 인프라가 닿는 동작은 **실제 RabbitMQ · Redis · PostgreSQL**로만 검증한다.
> 인프라와 무관한 순수 로직(직렬화 · 검증 규칙 · 상태 전이 표 · 작업 생애주기 · A* 등)만 인프라 없이 돈다.

| 층 | 대상 | 실행 조건 |
|---|---|---|
| **순수 로직** | 아래 "순수 로직 테스트" 표 | 항상. `dotnet test` 기본 |
| **통합** | 아래 "통합 테스트" 표 — 실제 서버 | 접속 정보가 있을 때. 없으면 **skip으로 표시**(실패 아님) |
| **E2E(수동)** | X-01~X-04 | 사람이 실행 · 보고서 첨부 |

### 공통 장치

| 장치 | 결정 | 근거 |
|---|---|---|
| skip | `[IntegrationFact("FLEET_TEST_RABBITMQ", …)]` — 인자는 **설정 이름**. `TestEnv.Get(이름)`이 하나라도 비면 `Skip = "{이름} 없음"`. `[Trait("Category","Integration")]` | 추가 테스트 패키지 없이 xUnit 2.9에서 **skip으로 표시**된다 |
| 설정 이름 | `FLEET_TEST_RABBITMQ`(AMQP URI) · `FLEET_TEST_REDIS` · `FLEET_TEST_PG` · `FLEET_TEST_RABBITMQ_MGMT`(관리 API URL, 선택) | 09 |
| 값 출처 (앞이 이김) | ① 같은 이름의 환경 변수 ② `tests/appsettings.Test.Local.json`(**gitignore, 사용자가 실제 값**) ③ `tests/appsettings.Test.json`(커밋, 네 키 모두 빈 문자열). 빈 문자열 = 없음 | 사용자는 파일에 넣고, CI는 환경 변수로 넣는다 |
| 파일 찾기 | `AppContext.BaseDirectory`에서 위로 올라가 `RobotFleet.sln`이 있는 폴더 아래 `tests/` | 어느 테스트 프로젝트든 같은 파일 |
| 실행 id | `t{yyyyMMddHHmmss}{4자리 난수}` | 공용 서버를 여러 실행이 나눠 쓴다 |
| 격리 | RabbitMQ: 토폴로지 접두사 `{runId}.` / Redis: 키 접두사 `{runId}:` / PostgreSQL: 스키마 `t_{runId}` + `search_path` | vhost 생성은 관리자 권한이 필요하다. 접두사는 일반 권한으로 된다 |
| 정리 | 시험 끝에 삭제 + **1시간 지난 `t…` 자원 청소** — Redis · PostgreSQL은 항상, RabbitMQ는 `FLEET_TEST_RABBITMQ_MGMT`가 있을 때만 | 중간에 죽은 실행의 잔여물이 다음 실행을 오염시키지 않게 |
| 시험대 | `PipelineRig` — 실제 `IngestService` · `AssignmentCoordinator` · `PgEventStore` · Rabbit 발행 · 구독을 접두사 · 스키마로 조립. 송신 시험은 **실제 Kestrel**에 운영과 같은 `AddFleetIngest`로 IngestApi 엔드포인트를 띄운다 | 운영 조립과 같은 코드를 실제 서버에 붙인다 |
| 계약 추상 클래스 | `tests/Infrastructure.Tests/Contracts/*Contract.cs` — 실제 구현(Redis · Rabbit · Pg) 실행 클래스만 있다 | 표를 한 곳에 두고 구현별로 돌린다 |
| 장애 만들기 | 대역 대신 실제 수단: 워커 `KillAsync`(ack 없이 채널 닫기), `LOCK TABLE`(커밋 정지), Redis에 다른 인스턴스의 `pending` 키 선기록(503), 깨진 메시지 직접 발행(독 메시지), 21회 반환(전달 한도) | 실패 경로를 실제 브로커 · DB 의미론으로 본다 |

### 순수 로직 테스트 (인프라 없음, 항상)

| ID | 대상 | 검증 |
|---|---|---|
| M-01~M-04 | 계약 | 03 예시 JSON 왕복 · 라우팅 키 표 · `eventId` · `TopologySpec` 인자(바인딩 49) |
| I-16 | Ingest 검증 | 400 사유 표(버전 · UUIDv7 · 개수 · 모르는 타입 · 라우팅 필드 · tickMs · mapId · JSON) |
| I-msg | Ingest 메시지 생성 | 배열 순서 · `eventId` · 라우팅 키 · 범주 · 지속성 · `tickMs` 복사 |
| G-01~G-06 | 생성기 · 할당기 · 생애주기 | 시드 재현 · 포아송 평균 · 최근접 할당 · 전이 사유 순서 · 출발지 할당 · 경로 없음 |
| E-01~E-04 | 수집기 | 배치 안 순서 · 대기 구간당 ConflictDetected 1건 · 1000건 분할 · 전이 표 안 (이벤트는 `FleetSimulation.CaptureEvents`로 받는다) |
| S-04 | 송신기 | 버퍼 가득 → 틱 정지(송신 루프를 돌리지 않는다) |
| S-log | 송신 로그 | 이벤트당 1행 형식 |
| 기타 | TestRun · TestEnv · SendLogReader · ReconcileOptions · 상태 전이 표 · UUIDv7 · Hosts H-01~H-05 | |

### 통합 테스트 (실제 서버, 접속 정보 있을 때)

| ID | 서버 | 검증 |
|---|---|---|
| C-01 | Redis | `IIdemStore` 계약 — 신규 · done · 5초 미만 BUSY · 5초 이상 TAKEOVER · 내 pending만 Abort · SET XX (Lua 원자성 포함) |
| C-02 | Redis | `IPartitionRoster` 계약 — id 순 순위 · 30초 만료 · 탈퇴 |
| C-03 | PostgreSQL | `IEventStore` 계약 — W-10 telemetry 중복 집계 1회 · W-11 TaskCompleted 1회 · W-12 단조 · 위반 영수증 · conflict 중복 |
| C-04 | RabbitMQ | 같은 키 한 큐 순서 · prefetch · `multiple` ack · SAC 활성 전환과 채널 닫기 반환 · reject → DLQ · invalid 큐 발행 |
| T-01~T-03 | RabbitMQ | 두 번 선언 성공 · 인자 다른 기존 큐 → 기동 실패(406) · 키 분산 · 21회 반환 → DLQ · 1000건 발행 순서 |
| D-01~D-04 | PostgreSQL | 마이그레이션 · 동시 기동 · 파티션 · `unnest` RETURNING |
| I-01 · I-02 | Rabbit · Redis | 신규 202 · done · 재전송 202 중복(큐 증가 없음) |
| I-03 | Rabbit · Redis | 다른 인스턴스 pending → 503 `Retry-After: 1` |
| I-07 | Rabbit | Redis 미설정 → 1단계 생략, 202 |
| I-15 | Rabbit · Redis | 같은 로봇 이벤트가 배열 순서대로 큐에, `tickMs` 복사 |
| W-02 | Rabbit · Pg | **ack는 커밋 뒤에만**: `LOCK TABLE`로 커밋을 멈춘 채 워커 kill → 3건 전부 반환 → 다음 워커가 적재 |
| W-05 | Rabbit · Pg | 처리 중 kill → 재처리해도 행 1회 |
| W-06 | Rabbit · Pg | 깨진 메시지 1건만 DLQ |
| W-08 | Rabbit · Pg | 금지 전이 → `valid = false` 영수증 · `robot_state` 불변 · `fleet.invalid` 발행 |
| W-09 | Rabbit · Pg | 같은 eventId 재전달 → 오탐 없음 |
| R-01 · R-05 | 셋 다 | 실제 명부로 `p % N == rank`, 2회 연속일 때만 적용 |
| R-04 | 셋 다 | 로봇 40대 × 140단계 상태 전이, 합류 · 탈퇴 재배정 → invalid 0 |
| R-07 | Rabbit · Pg | 명부 없음 → 단일 워커가 16 파티션 전부 |
| **R-run-01** | Pg (저장소) · Rabbit · Pg (워커 전이 검증) | 같은 `robotId`가 run A · B에 각각 적재되고 `robot_state`가 **run별로 분리**된다. run B 첫 전이에서 `invalid` 0 |
| **R-run-02** | Pg | 같은 `taskId`가 run A(Completed) · B(Pending)에 존재하고, B가 **단조 upsert에 막히지 않는다** |
| **R-run-03** | Pg | `SimulationStarted`보다 telemetry가 먼저 도착해도 적재 성공(FK 없음), 이후 메타가 채워진다 |
| **R-run-04** | Pg | Reconcile 차집합이 **run 경계를 넘지 않는다**(run A의 이벤트를 run B 누락으로 세지 않는다) |
| S-01 · S-02 · S-06 | 셋 다 | 실제 IngestApi가 503(다른 인스턴스 pending)을 주는 동안 다음 배치를 보내지 않고, 같은 batchId로 재전송, 송신 로그는 202 뒤에만 |
| E2E | 셋 다 | Simulator(20대 · 600틱) → HTTP IngestApi → RabbitMQ → 워커(300틱에 kill → 교대) → PostgreSQL → Reconcile 질의: task · conflict · telemetry 누락 0, invalid 0, task_history 중복 0 |

대역 없이는 만들 수 없어 **삭제한** 검증: I-06 · I-09~I-14(브로커 nack · 확인 타임아웃 · 채널 끊김 · reject-publish 주입), W-03 · W-04(저장 일시 오류 · 커밋 후 예외 주입), S-03 · S-05 · S-07(응답 각본 · 느린 응답). 코드 경로는 남아 있고, M8 장애 주입에서 실제 장애로 확인한다.

### E2E (수동, 보고서 첨부)

| ID | 시나리오 | 완료 기준 |
|---|---|---|
| X-01 | 200대 · 10Hz · 30분, 워커 강제 종료 2회 + Ingest 재시작 1회 → Reconcile | 1 · 2 · 6 |
| X-02 | 워커 2대 기동 → 30분 중 3대째 추가 · 제거(재배정) | 3 |
| X-03 | X-01 동안 큐 적체 · `fleet_event_lag_seconds` p99 | 4 |
| X-04 | 같은 시드로 파이프라인 없이 / 있이 → `fleet_tick_overrun_total` 비교 | 5 |

### 완료 기준 ↔ 증명 대응 (09 승인 조건 1)

| 기준 | 코드 | 테스트 | 스크립트 · 도구 |
|---|---|---|---|
| 1 task · conflict 유실 0 | `IngestService` 확인 판정, `EventBatchSender`, `PartitionProcessor` | W-02 · W-05, S-01 · S-02 · S-06, E2E | `src/Reconcile` + X-01 |
| 2 중복 0 | `PgEventStore`(RETURNING 집계), `robot_status_log` | C-03, W-05 · W-09, I-02, E2E | 메트릭 `outcome="duplicate"` vs 행 수, X-01 |
| 3 순서 유지 | `AssignmentCoordinator`, SAC 토폴로지, `StatusTransitions` 검증 | R-04, W-08, C-04, I-15 | Reconcile `invalidTransitions` · `fleet_invalid_depth`, X-02 |
| 4 처리량 · 지연 | 배치 창 · `unnest` · 확인 몰아받기 | D-04, T 발행 순서 | `fleet_event_lag_seconds` · `fleet_queue_depth`, X-03 |
| 5 틱 예산 | 버퍼 + 백그라운드 송신 | S-04 | `fleet_tick_overrun_total`, X-04 |
| 6 telemetry 유실 계측 | 범주별 확인 타임아웃 | E2E(telemetry 누락 수) | `fleet_telemetry_dropped_total` + Reconcile telemetry 행 |
| 7 테스트 | `IntegrationFact` · 계약 추상 클래스 · 실행 id 청소 | 순수 로직 전부 · 통합 전부 | `dotnet test`(서버 없으면 통합 skip) |

### "처음 띄울 때" 경로 (09 승인 조건 5)

| 순서 | 주체 | 동작 | 실패 시 |
|---|---|---|---|
| 1 | EventWorker | PostgreSQL 연결 → advisory lock → 마이그레이션 → 당일 · 익일 파티션 | 기동 실패 (재시작 정책이 재시도) |
| 2 | EventWorker · IngestApi | RabbitMQ 연결 → 토폴로지 선언(exchange → 큐 → 바인딩) | EventWorker는 기동 실패, IngestApi는 2초마다 재시도하며 Not Ready |
| 3 | EventWorker | 명부 등록 → 몫 계산 → 소비 시작 → Ready | Redis 미설정 · 불통이면 48큐 전부(§7) |
| 4 | IngestApi | Ready (Redis 연결은 첫 사용 시) | — |
| 5 | Simulator | `--ingest` 대상에 송신. Ingest가 아직 Not Ready면 503 · 연결 실패 → 재시도 | 버퍼가 차면 틱 정지 |

> Ingest가 워커보다 먼저 떠도 안전하다. 토폴로지를 Ingest도 선언하므로 메시지는 큐에 쌓이고, 워커가 마이그레이션을 끝낸 뒤 소비한다.

### 실서버 첫 실행 점검표 (SQL은 PostgreSQL에서 한 번도 실행된 적 없음)

모든 통합 테스트(47건)는 접속 정보가 없어 skip이고, `unnest` · `RETURNING` · `run_metrics` CTE · 파티션 SQL은 **실제 서버에서 돈 적이 없다.** 사용자가 접속 정보를 넣고 통합 테스트를 돌릴 때 아래를 점검표로 쓴다. 각 항목은 "타입 추론 · 파티션 · ON CONFLICT 의미"처럼 **빈 DB 단위 테스트는 통과해도 실데이터에서 깨질 수 있는 지점**이다.

| # | 위치 | 확인할 것 |
|---|---|---|
| 1 | `PgEventStore.cs` telemetry INSERT(`unnest` · `RETURNING run_id, map_code, robot_id, tick, status, occurred_at`) | 파티션 테이블(`robot_telemetry`) 대상 `ON CONFLICT DO NOTHING … RETURNING`이 DEFAULT · 날짜 파티션 양쪽에서 동작. nullable `smallint`(`battery`만 타입 명시) 배열 타입 추론 |
| 2 | `PgEventStore.cs` telemetry `unnest` 배열 파라미터 | `battery`만 `NpgsqlDbType.Array \| Smallint` 명시, 나머지(`text[]` · `bigint[]` · `smallint[]` · `timestamptz[]`)는 추론 — 빈 배열 · nullable 섞임에서 타입 모호성 없는지 |
| 3 | `PgEventStore.cs` task_history UPSERT(`NpgsqlBatchCommand` 이름 파라미터) | `COALESCE(@type smallint, 0)`의 `@type`이 `smallint`로, `@robot`이 `DBNull`일 때 타입 추론, `WHERE EXCLUDED.status > task_history.status` 단조 조건 |
| 4 | `PgEventStore.cs` conflict_log · replan_log `unnest`(task `text[]` 11개 배열) | `@task` nullable `text[]`, `compute_ms` `real[]` 타입, replan 11개 배열 길이 일치 |
| 5 | `PgEventStore.cs` simulations UPSERT `params`(`Jsonb`) · `CheckMapHashAsync` | `JsonSerializer.Serialize` 결과가 `jsonb`로 들어가는지, 빈 `maps` 기준정보에서 대조 생략 |
| 6 | `ReconcileQueries.cs` `RunMetricsSql` CTE | `percentile_cont(...) FILTER`, `::real` · `::int` 형 변환, `NULLIF` 0 나눗셈, `LEFT JOIN s ON true`에서 `complete = (s.ended_at IS NOT NULL)`가 simulations 행 없을 때 false |
| 7 | `ReconcileQueries.cs` 차집합(`sent` 임시 테이블 binary COPY) | `s.category` · `s.event_type` · `s.key` 컬럼, run 경계 조건(`t.run_id = s.run_id`) |
| 8 | `003_partitions.sql` DEFAULT 파티션 | DEFAULT에 행이 생긴 날짜의 파티션 생성(`CREATE TABLE … PARTITION OF … FOR VALUES`)이 겹침으로 실패하는지(§8 "사람이 옮긴다") |
| 9 | `Migrator.cs` 체크섬 | 001 · 002가 개발 중 재작성됐다 — 적용된 적 없는 빈 서버라 첫 적용은 통과하지만, 한 번 적용한 뒤 파일을 고치면 `MigrationChecksumException` |
| 10 | 결정적 오류 격리(이번 추가) | 일부러 제약 위반 메시지 1건을 섞어 DLQ로 1건만 가고 나머지 적재, `fleet_worker_deterministic_dlq_total` 증가, 파티션이 안 서는지(W 계열에 추가 가능) |

> 통합 테스트를 돌리면 1~9는 해당 `[IntegrationFact]`가 덮는다. 10은 아직 테스트가 없다(실서버 격리 테스트로 추가할 후보).

---

## 13. 작업 분할

`[핵심]` = 중간 관리자(Opus). `[단순 S1~S4]` = 하위 에이전트(Sonnet), 최대 4명 병렬. **묶음끼리 파일이 겹치지 않는다.**

| 차수 | 묶음 | 선행 |
|---|---|---|
| 1차 (병렬) | S1 · S2 · S4 | 없음. S2 · S4는 S1의 **시그니처(아래 코드 블록)** 에 맞춰 쓴다 — S1 산출물이 없어도 같은 이름 · 같은 형태로 컴파일되게 |
| 병합 | 중간 관리자 | 1차 셋을 합치고 `RobotFleet.sln`에 프로젝트 추가, 전체 빌드 |
| 2차 | S3 · [핵심] (병렬) | 병합 완료 + [핵심] 껍데기 커밋. S3는 껍데기 시그니처만 보고 컴파일한다 |

1차 공통 규칙:

| 규칙 | 근거 |
|---|---|
| `RobotFleet.sln`은 아무도 고치지 않는다. 자기 csproj를 `dotnet build <csproj>`로 직접 빌드 | 세 묶음이 같은 파일을 고치면 충돌 |
| `Directory.Build.props`(net8.0 · nullable · 경고 = 오류)를 따른다. 패키지 버전은 아래 표 그대로 | 묶음 간 버전 불일치 방지 |
| 접속 정보 · 비밀번호를 코드 · 설정 · 주석 · 커밋 메시지에 넣지 않는다 | 09 |
| 파일 상단 주석은 근거가 되는 문서 절(예: `// 10 §8`)만 | |

| 패키지 | 버전 |
|---|---|
| `RabbitMQ.Client` | 7.1.2 |
| `StackExchange.Redis` | 2.8.16 |
| `Npgsql` | 8.0.5 |
| `prometheus-net.AspNetCore` | 8.2.1 |
| `Microsoft.Extensions.TimeProvider.Testing` | 8.10.0 |
| xunit · FluentAssertions · Test.Sdk | 기존 테스트 프로젝트와 같은 버전 |

### [단순 S1] 계약

**만들 파일** (프로젝트 `src/Shared/RobotFleet.Shared.csproj`, 외부 패키지 추가 금지)

| 파일 | 내용 |
|---|---|
| `src/Shared/Messaging/FleetSchema.cs` | `public static class FleetSchema { public const int Version = 1; public const int MaxEventsPerBatch = 1000; }` |
| `src/Shared/Messaging/EventType.cs` | 03 이벤트 12종 문자열 상수 + `IReadOnlySet<string> All` |
| `src/Shared/Messaging/EventCategory.cs` | `enum EventCategory { Telemetry, Task, Conflict }`, `EventCategories.Of(string eventType)`, `EventCategories.IsPersistent(string eventType)` — 03 "이벤트 → 큐 매핑" 표 |
| `src/Shared/Messaging/EventBatch.cs` | 배치 · 항목 봉투 |
| `src/Shared/Messaging/Payloads.cs` | 페이로드 record 12종 + `CellDto` |
| `src/Shared/Messaging/FleetJsonContext.cs` | 소스 생성 컨텍스트 + `FleetJson` 도우미 |
| `src/Shared/Messaging/RoutingKeys.cs` | 01 라우팅 키 표 |
| `src/Shared/Messaging/TopologySpec.cs` | 08 §8 인자 표를 데이터로 |
| `src/Shared/Pipeline/*.cs` | 인터페이스 6종 + 주고받는 record (파일당 인터페이스 1개) |
| `src/Shared/Domain/StatusTransitions.cs` | 03 상태 전이 표 전체 |
| `tests/Shared.Tests/Messaging/{EnvelopeJsonTests,RoutingKeyTests,TopologySpecTests,StatusTransitionTests}.cs` | M-01~M-04 + 전이 표 |

**공개 시그니처**

```csharp
namespace RobotFleet.Shared.Messaging;

public sealed record EventBatch(
    int SchemaVersion, string BatchId, string MapId, string RunId, long Tick, int TickMs,
    DateTimeOffset OccurredAt, IReadOnlyList<EventItem> Events);

public sealed record EventItem(string EventType, JsonElement Payload);

/// 큐 메시지 본문. eventId = "{batchId}/{index}"
public sealed record EventEnvelope(
    int SchemaVersion, string EventId, string EventType, string MapId, string RunId, long Tick, int TickMs,
    DateTimeOffset OccurredAt, JsonElement Payload)
{
    public static EventEnvelope From(EventBatch batch, int index);
}

public sealed record CellDto(int X, int Y);
public sealed record RobotTelemetryPayload(string RobotId, int X, int Y, int Heading, int Status, int Battery,
    string? TaskId, int PathIndex, int PathLength);
public sealed record TaskCreatedPayload(string TaskId, int Type, CellDto Origin, CellDto Destination, int Priority);
public sealed record TaskAssignedPayload(string TaskId, string RobotId, int PlannedPathLength, int EstimatedDurationMs);
public sealed record TaskStartedPayload(string TaskId, string RobotId);
public sealed record TaskCompletedPayload(string TaskId, string RobotId, int ActualSteps, int MoveMs, int WaitMs,
    int WorkMs, int ReplanCount);
public sealed record TaskFailedPayload(string TaskId, string? RobotId, string Reason, int AtX, int AtY);
public sealed record ConflictDetectedPayload(string ConflictType, int X, int Y, IReadOnlyList<string> RobotIds,
    string WinnerRobotId, string Resolution, int WaitMs);
public sealed record DeadlockDetectedPayload(IReadOnlyList<string> Cycle, IReadOnlyList<CellDto> Cells,
    string Resolution, string YieldRobotId, int YieldToX, int YieldToY, int ResolvedMs);
public sealed record PathReplannedPayload(string RobotId, string? TaskId, string Reason, int OldLength,
    int NewLength, double ComputeMs);
public sealed record RobotStatusChangedPayload(string RobotId, int From, int To, string Reason);

public static class FleetJson
{
    public static JsonSerializerOptions Options { get; }                 // FleetJsonContext.Default.Options
    public static byte[] Serialize<T>(T value);                          // 소스 생성 컨텍스트만 사용
    public static T Deserialize<T>(ReadOnlySpan<byte> utf8);
    public static JsonElement ToElement<T>(T payload);
    public static T PayloadOf<T>(EventEnvelope envelope);
}

public static class RoutingKeys
{
    /// telemetry → robotId, Task* → taskId, RobotStatusChanged → robotId, conflict 범주 → mapId
    public static string Of(string eventType, JsonElement payload, string mapId);
}

public sealed record ExchangeSpec(string Name, string Type, bool Durable);
public sealed record QueueSpec(string Name, EventCategory? Category, int? Partition,
    IReadOnlyDictionary<string, object> Arguments);
public sealed record BindingSpec(string Exchange, string Queue, string RoutingKey);   // consistent-hash 가중치 "1"
public sealed record TopologySpec(IReadOnlyList<ExchangeSpec> Exchanges, IReadOnlyList<QueueSpec> Queues,
    IReadOnlyList<BindingSpec> Bindings)
{
    public const int Partitions = 16;
    public static TopologySpec Build(string prefix = "");
    public static string ExchangeName(EventCategory category, string prefix = "");    // "{prefix}fleet.task"
    public static string QueueName(EventCategory category, int partition, string prefix = ""); // "{prefix}fleet.task.p03"
    public static string DlxName(string prefix = "");       // "{prefix}fleet.dlx"  (fanout)
    public static string DlqName(string prefix = "");       // "{prefix}fleet.dlq"  (quorum)
    public static string InvalidQueueName(string prefix = ""); // "{prefix}fleet.invalid" (quorum, 기본 exchange로 발행)
}
```

`TopologySpec.Build` 인자 (숫자는 `long`, 불리언은 `bool`):

| 큐 | 인자 |
|---|---|
| task · conflict 32 | `x-queue-type=quorum`, `x-single-active-consumer=true`, `x-delivery-limit=20`, `x-dead-letter-exchange={prefix}fleet.dlx`, `x-dead-letter-strategy=at-least-once`, `x-overflow=reject-publish`, `x-max-length-bytes=67108864` |
| telemetry 16 | `x-queue-type=classic`, `x-single-active-consumer=true`, `x-overflow=drop-head`, `x-max-length=20000` |
| `fleet.dlq` · `fleet.invalid` | `x-queue-type=quorum` |

```csharp
namespace RobotFleet.Shared.Pipeline;

public interface IEventSink                       // Simulator 틱 루프 → 송신 버퍼
{
    bool TryWrite(EventBatch batch);              // 대기 없음
    ValueTask<bool> WaitToWriteAsync(CancellationToken ct);   // false = 닫힘
}

public enum IngestStatus { Accepted, Duplicate, Unavailable, Invalid }
public sealed record IngestResult(IngestStatus Status, int TelemetryDropped = 0, string? Error = null);
public interface IIngestClient
{
    Task<IngestResult> SendAsync(EventBatch batch, ReadOnlyMemory<byte> body, CancellationToken ct);
}

public enum IdemBeginStatus { Begin, Takeover, Done, Busy, Bypass }   // Bypass = 저장소 불통(fail-open)
public readonly record struct IdemBegin(IdemBeginStatus Status, long PendingSinceMs);
public interface IIdemStore
{
    Task<IdemBegin> TryBeginAsync(string batchId, CancellationToken ct);
    Task CompleteAsync(string batchId, CancellationToken ct);
    Task AbortAsync(string batchId, long pendingSinceMs, CancellationToken ct);   // 내 pending일 때만 삭제
}

public sealed record OutgoingMessage(EventCategory Category, string RoutingKey, string MessageId,
    string EventType, bool Persistent, ReadOnlyMemory<byte> Body);
public sealed record PublishOutcome(int ReliableConfirmed, int ReliableFailed,
    int TelemetryConfirmed, int TelemetryDropped, IReadOnlyDictionary<string, int> DropReasons);
public interface IEventPublisher
{
    Task<PublishOutcome> PublishBatchAsync(IReadOnlyList<OutgoingMessage> messages, CancellationToken ct);
    Task PublishToQueueAsync(string queue, OutgoingMessage message, CancellationToken ct);   // fleet.invalid. 확인 실패 시 예외
}

public sealed record Delivery(string Queue, ulong DeliveryTag, string MessageId, string EventType,
    int SchemaVersion, ReadOnlyMemory<byte> Body, bool Redelivered, long DeliveryCount);
public interface IPartitionSubscription : IAsyncDisposable    // Dispose = 취소 + 채널 닫기(미확인 반환)
{
    string Queue { get; }
    Task AckUpToAsync(ulong deliveryTag, CancellationToken ct);   // multiple: true
    Task RejectAsync(ulong deliveryTag, CancellationToken ct);    // requeue: false → DLX
}
public interface IPartitionSubscriber
{
    Task<IPartitionSubscription> SubscribeAsync(string queue, ushort prefetch,
        Func<Delivery, CancellationToken, ValueTask> onDelivery, CancellationToken ct);
    Task<long> GetMessageCountAsync(string queue, CancellationToken ct);
}

public sealed record TelemetryRow(string MapId, string RobotId, long Tick, short X, short Y, short Heading,
    short Status, short? Battery, DateTimeOffset OccurredAt, int TickMs);
public sealed record TaskEventRow(string EventId, string MapId, string TaskId, string EventType, long Tick,
    DateTimeOffset OccurredAt, JsonElement Payload);
public sealed record StatusChangeRow(string EventId, string MapId, string RobotId, long Tick,
    DateTimeOffset OccurredAt, short From, short To, string Reason, bool Valid);
public sealed record ConflictRow(string EventId, string MapId, long Tick, DateTimeOffset OccurredAt,
    string EventType, JsonElement Payload);
public sealed record StoreBatch(IReadOnlyList<TelemetryRow> Telemetry, IReadOnlyList<TaskEventRow> TaskEvents,
    IReadOnlyList<StatusChangeRow> StatusChanges, IReadOnlyList<ConflictRow> Conflicts);
public sealed record StoreResult(int TelemetryInserted, int TelemetryDuplicate, int TaskInserted, int TaskDuplicate,
    int StatusInserted, int StatusDuplicate, int ConflictInserted, int ConflictDuplicate);
public sealed record RobotStateRow(string MapId, string RobotId, short Status, long LastTick, string LastEventId);
public interface IEventStore
{
    Task<StoreResult> StoreBatchAsync(StoreBatch batch, CancellationToken ct);   // 트랜잭션 1개
    Task<IReadOnlyDictionary<string, RobotStateRow>> GetRobotStatesAsync(string mapId,
        IReadOnlyCollection<string> robotIds, CancellationToken ct);
    Task<IReadOnlySet<string>> GetExistingStatusEventIdsAsync(IReadOnlyCollection<string> eventIds, CancellationToken ct);
}

public sealed record RosterView(IReadOnlyList<string> Members)   // id 문자열 오름차순
{
    public int RankOf(string workerId);                          // 없으면 -1
}
public interface IPartitionRoster
{
    bool IsConfigured { get; }
    Task<RosterView?> HeartbeatAsync(string workerId, CancellationToken ct);   // null = 불통
    Task LeaveAsync(string workerId, CancellationToken ct);
}
```

```csharp
namespace RobotFleet.Shared.Domain;
public static class StatusTransitions
{
    public static IReadOnlyList<(RobotStatus From, RobotStatus To, string Reason)> All { get; }   // 03 표 전부
    public static bool IsAllowed(RobotStatus from, RobotStatus to, string reason);
}
```

**완료 조건**

| 조건 |
|---|
| `dotnet build src/Shared/RobotFleet.Shared.csproj` 경고 0 |
| `dotnet test tests/Shared.Tests` 전부 통과, 기존 테스트 회귀 없음 |
| M-01: 03의 배치 봉투 예시 JSON(`tickMs` 포함) → 역직렬화 → 직렬화가 공백 제외 같은 바이트, 속성 순서 = 선언 순서 |
| M-02: 12종 각각 라우팅 키 기대값 표(테스트에 표로) |
| M-03: `EventEnvelope.From(batch, 17).EventId == $"{batchId}/17"`, `TickMs` 복사 |
| M-04: `Build("")` → exchange 4 · 큐 50 · 바인딩 49(DLX → DLQ 포함), 위 인자 표와 일치, `Build("t1.")` → 모든 이름 접두사 |
| 전이: 03 표의 허용 칸 전부 `true`, 금지 예시 5개 `false` |

### [단순 S2] DB

**만들 파일**

| 파일 | 내용 |
|---|---|
| `db/migrations/001_schema.sql` | 01 PostgreSQL 테이블 그대로 — **`simulations`와 모든 `run_id` 컬럼 · 키, `replan_log` · `run_metrics`, 기준정보 7개 테이블이 `001`에 포함된다. 별도 마이그레이션을 만들지 않는다.** 날짜 파티션은 만들지 않는다 |
| `db/migrations/002_logs_and_state.sql` | 아래 DDL |
| `db/migrations/003_partitions.sql` | 파티션 함수 + DEFAULT 파티션 |
| `src/Infrastructure.Postgres/RobotFleet.Infrastructure.Postgres.csproj` | `Npgsql`, `Microsoft.Extensions.Logging.Abstractions`, Shared 참조. `../../db/migrations/*.sql`을 `migrations/`로 출력 복사 |
| `src/Infrastructure.Postgres/Migrations/Migrator.cs` | 아래 |
| `src/Infrastructure.Postgres/Migrations/PartitionMaintainer.cs` | 아래 |
| `tests/Infrastructure.Tests/RobotFleet.Infrastructure.Tests.csproj` | Postgres · TestSupport(S4) 참조 |
| `tests/Infrastructure.Tests/Postgres/{MigrationTests,PartitionTests}.cs` | D-01~D-03 (L2) |

```sql
-- 002_logs_and_state.sql
CREATE TABLE task_event_log (
    event_id    TEXT PRIMARY KEY,
    run_id      TEXT NOT NULL,
    map_code    TEXT NOT NULL,
    task_id     TEXT NOT NULL,
    event_type  TEXT NOT NULL,
    tick        BIGINT NOT NULL,
    occurred_at TIMESTAMPTZ NOT NULL
);
CREATE INDEX ix_task_event_log_task ON task_event_log (task_id);
CREATE INDEX ix_task_event_log_run ON task_event_log (run_id);

CREATE TABLE robot_status_log (
    event_id    TEXT PRIMARY KEY,
    run_id      TEXT NOT NULL,
    map_code    TEXT NOT NULL,
    robot_id    TEXT NOT NULL,
    tick        BIGINT NOT NULL,
    from_status SMALLINT NOT NULL,
    to_status   SMALLINT NOT NULL,
    reason      TEXT NOT NULL,
    valid       BOOLEAN NOT NULL,          -- 전이 검증 결과 (10 §6)
    occurred_at TIMESTAMPTZ NOT NULL
);
CREATE INDEX ix_robot_status_log_run ON robot_status_log (run_id);
CREATE INDEX ix_robot_status_log_invalid ON robot_status_log (run_id) WHERE NOT valid;

CREATE TABLE robot_state (
    run_id        TEXT NOT NULL,
    robot_id      TEXT NOT NULL,
    map_code      TEXT NOT NULL,
    status        SMALLINT NOT NULL,
    last_tick     BIGINT NOT NULL,
    last_event_id TEXT NOT NULL,
    PRIMARY KEY (run_id, robot_id)
);

-- 003_partitions.sql
CREATE TABLE IF NOT EXISTS robot_telemetry_default PARTITION OF robot_telemetry DEFAULT;
CREATE OR REPLACE FUNCTION fleet_ensure_telemetry_partition(day date) RETURNS text
LANGUAGE plpgsql AS $$
DECLARE name text := format('robot_telemetry_%s', to_char(day, 'YYYYMMDD'));
BEGIN
    EXECUTE format('CREATE TABLE IF NOT EXISTS %I PARTITION OF robot_telemetry FOR VALUES FROM (%L) TO (%L)',
                   name, day::timestamp AT TIME ZONE 'UTC', (day + 1)::timestamp AT TIME ZONE 'UTC');
    RETURN name;
END $$;
```

> 날짜 경계는 UTC다. `day::timestamp AT TIME ZONE 'UTC'`가 그날 UTC 자정의 `timestamptz`다.

```csharp
namespace RobotFleet.Infrastructure.Postgres.Migrations;

public sealed class Migrator(NpgsqlDataSource dataSource, string migrationsDirectory, ILogger<Migrator> logger)
{
    /// advisory lock(hashtext('fleet:migrations')) 안에서 schema_migrations에 없는 파일을 이름 순으로,
    /// 파일마다 트랜잭션 1개. 적용된 파일의 SHA-256이 다르면 MigrationChecksumException. 반환 = 이번에 적용한 버전
    public Task<IReadOnlyList<string>> ApplyAsync(CancellationToken ct);
}
public sealed class MigrationChecksumException(string version) : Exception;

public sealed class PartitionMaintainer(NpgsqlDataSource dataSource, TimeProvider time, ILogger<PartitionMaintainer> logger)
{
    public Task EnsureAsync(CancellationToken ct);              // UTC 당일 · 익일
    public Task<long> CountDefaultRowsAsync(CancellationToken ct);
    public Task RunHourlyAsync(CancellationToken ct);            // 매시 정각 EnsureAsync + CountDefaultRows
}
```

`schema_migrations(version text PRIMARY KEY, checksum text NOT NULL, applied_at timestamptz NOT NULL DEFAULT now())`는 Migrator가 첫 실행 때 만든다. 스키마는 연결 문자열의 `search_path`를 따른다(테스트 격리).

**완료 조건**

| 조건 |
|---|
| `dotnet build src/Infrastructure.Postgres` 경고 0 |
| `dotnet build tests/Infrastructure.Tests` — S4 병합 후 확인(1차 단독으로는 Postgres 프로젝트까지) |
| D-01~D-03이 `[IntegrationFact("FLEET_TEST_PG")]` + `[Trait("Category","Integration")]`로 작성되고, 각 테스트가 `TestRun.PgSchema` 스키마를 만들고 지운다 |
| SQL 3개가 빈 데이터베이스에 이름 순으로 적용 가능(사람이 원격 서버에서 1회 확인 — 중간 관리자) |

### [단순 S3] 호스트 조립 (2차)

[핵심]의 업무 로직은 호스트 없는 라이브러리 프로젝트에 있다(아래 "S3가 쓰는 [핵심] 타입"). 그 타입들은 **시그니처가 확정된 껍데기로 이미 커밋돼 있어** S3 혼자 컴파일된다. S3는 등록 · 호출 · HTTP 배선만 하고, 본문 구현(`NotImplementedException`)은 [핵심]이 병렬로 채운다.

**만들 파일** (이 밖의 파일은 고치지 않는다. 특히 `src/Ingest.Core` · `src/EventWorker.Core` · `src/Infrastructure.*` · `src/Simulator/Fleet` · `src/Simulator/Events`)

| 파일 | 내용 |
|---|---|
| `src/Hosting/RobotFleet.Hosting.csproj` | 클래스 라이브러리. `Microsoft.Extensions.Configuration.Json` 8.0.1 · `…Configuration.EnvironmentVariables` 8.0.0 · `…Configuration.CommandLine` 8.0.0 · `…Hosting.Abstractions` 8.0.1, 참조: Ingest.Core · EventWorker.Core · Infrastructure.RabbitMq · Infrastructure.Redis · Infrastructure.Postgres |
| `src/Hosting/FleetConfiguration.cs` | §9 "접속 정보" 순서 적용 |
| `src/Hosting/FleetHostingExtensions.cs` | `AddFleetIngest` · `AddFleetWorker` |
| `src/Hosting/PartitionMaintenanceService.cs` | 매시 파티션 유지 + `fleet_telemetry_default_rows` 게이지 |
| `src/Hosting/HealthEndpoints.cs` | `/health/live` · `/health/ready` 매핑 |
| `src/IngestApi/RobotFleet.IngestApi.csproj` | `Microsoft.NET.Sdk.Web`. `prometheus-net.AspNetCore` 8.2.1, 참조: Hosting |
| `src/IngestApi/Program.cs`, `src/IngestApi/Endpoints/IngestEndpoints.cs` | 아래 |
| `src/IngestApi/appsettings.json` | 아래 JSON |
| `src/EventWorker/RobotFleet.EventWorker.csproj` | `Microsoft.NET.Sdk.Web`(관리 포트용). `prometheus-net.AspNetCore` 8.2.1, 참조: Hosting |
| `src/EventWorker/Program.cs`, `src/EventWorker/appsettings.json` | 아래 |
| `src/Simulator/SimulationOptions.cs`, `src/Simulator/Program.cs` | 옵션 추가 · fleet 모드 분기 (기존 M1 동작 유지) |
| `tests/Simulator.Tests/SimulationOptionsTests.cs` | 새 옵션 행 추가(기존 행 유지) |
| `tests/Hosts.Tests/RobotFleet.Hosts.Tests.csproj`, `ConfigurationTests.cs`, `CompositionTests.cs`, `HealthTests.cs` | H-01~H-05 |
| `RobotFleet.sln` | 위 새 프로젝트 4개(Hosting · IngestApi · EventWorker · Hosts.Tests) 추가 |

**S3가 쓰는 [핵심] 타입** (껍데기 커밋됨 — 고치지 말 것)

```csharp
// src/Shared/Pipeline
public interface IStartupStep { string Name { get; } Task RunAsync(CancellationToken ct); }
public sealed class ReadinessState { void Require(string c); void Set(string c, bool ready); bool IsReady { get; }
                                     IReadOnlyDictionary<string, bool> Snapshot(); }
public static class ReadinessComponents { RabbitTopology, PostgresMigrations, WorkerConsuming }

// src/Ingest.Core  (namespace RobotFleet.Ingest)
public sealed record IngestResponseBody(string? BatchId, int Accepted, int TelemetryDropped, bool Duplicate, string? Error);
public sealed record IngestResponse(int StatusCode, IngestResponseBody Body, int? RetryAfterSeconds) { IngestResult ToResult(); }
public sealed class IngestService { Task<IngestResponse> HandleAsync(ReadOnlyMemory<byte> body, CancellationToken ct); }
public static IServiceCollection AddIngestCore(this IServiceCollection services);           // IngestService · IngestStartupService · ReadinessState

// src/EventWorker.Core  (namespace RobotFleet.EventWorker)
public sealed class WorkerOptions { string WorkerId; string TopologyPrefix; ushort Prefetch; int BatchMax; TimeSpan BatchWindow; … }
public static IServiceCollection AddWorkerCore(this IServiceCollection services, Action<WorkerOptions>? configure = null);

// src/Infrastructure.RabbitMq · Redis · Postgres
public static IServiceCollection AddFleetRabbitMq(this IServiceCollection s, Action<RabbitMqOptions> c);  // ConnectionString 비면 예외. IStartupStep(토폴로지) 등록
public static IServiceCollection AddFleetRedis(this IServiceCollection s, Action<RedisOptions> c);        // 비면 아무것도 등록하지 않는다
public static IServiceCollection AddFleetPostgres(this IServiceCollection s, Action<PostgresOptions> c);  // 비면 예외. IStartupStep(마이그레이션 · 파티션) 등록
// RabbitMqOptions { ConnectionString, TopologyPrefix }, RedisOptions { ConnectionString, KeyPrefix }, PostgresOptions { ConnectionString }
// PartitionMaintainer { Task EnsureAsync(ct); Task<long> CountDefaultRowsAsync(ct); }

// src/Simulator
public sealed record FleetOptions { int Robots; double Lambda; int Seed; int WorkMs; int TickMs; double SpeedFactor;
                                    int MoveMsPerCell; int TurnMsPer90; long MaxTicks; }
public sealed record FleetRunResult(long Ticks, int TasksCreated, int TasksCompleted, int TasksFailed, long Overruns,
                                    TimeSpan StallTime, int ExitCode);
public sealed class FleetSimulation(GridMap map, FleetOptions options, IEventSink? sink, ITickDelay delay, ConsoleRenderer? renderer)
{ Task<FleetRunResult> RunAsync(CancellationToken ct); }
public sealed record EventSenderOptions { int Capacity; TimeSpan AttemptTimeout; …; string? FailureDumpDirectory; }
public sealed class EventBatchSender(IIngestClient client, SendLog? log, EventSenderOptions options, TimeProvider time) : IEventSink
{ bool Faulted; string? FaultReason; Task RunAsync(CancellationToken ct); Task<int> DrainAsync(CancellationToken ct); }
public sealed class SendLog(string directory, string mapId, string runId) : IDisposable { string Path; }
public sealed class HttpIngestClient(HttpClient http) : IIngestClient;   // http.BaseAddress = Ingest 주소
```

**S3 공개 시그니처**

```csharp
namespace RobotFleet.Hosting;

public static class FleetConfiguration
{
    /// 기존 소스 뒤에 appsettings.Local.json(optional, basePath 기준)을 붙이고,
    /// 그 뒤에 환경 변수 · 명령행을 다시 붙인다 → 순서: json < {Env}.json < Local.json < 환경 변수 < 명령행 (10 §9)
    public static IConfigurationBuilder AddFleetSources(this IConfigurationBuilder builder, string basePath, string[] args);

    /// 빈 문자열 · 공백 = null
    public static string? GetConnection(this IConfiguration config, string name);   // "RabbitMq" | "Redis" | "Postgres"
}

public static class FleetHostingExtensions
{
    /// AddFleetRabbitMq(ConnectionStrings:RabbitMq, Fleet:Topology:Prefix) → AddFleetRedis(ConnectionStrings:Redis,
    /// Fleet:Redis:KeyPrefix) → AddIngestCore()
    public static IServiceCollection AddFleetIngest(this IServiceCollection services, IConfiguration config);

    /// 등록 순서가 기동 순서다: AddFleetPostgres → AddFleetRabbitMq → AddFleetRedis →
    /// AddWorkerCore(WorkerId = Fleet:Worker:Id, TopologyPrefix = Fleet:Topology:Prefix) → PartitionMaintenanceService
    public static IServiceCollection AddFleetWorker(this IServiceCollection services, IConfiguration config);
}

/// postgres.migrations가 Ready가 된 뒤 시작, 매시 정각 EnsureAsync + CountDefaultRowsAsync → 게이지 fleet_telemetry_default_rows.
/// 실패는 로그만(다음 정각 재시도). TimeProvider 주입
public sealed class PartitionMaintenanceService : BackgroundService { … }

public static class HealthEndpoints
{
    /// /health/live → 200 "live". /health/ready → ReadinessState.IsReady ? 200 : 503, 본문 = Snapshot() JSON
    public static IEndpointRouteBuilder MapFleetHealth(this IEndpointRouteBuilder app);
    internal static (int StatusCode, IReadOnlyDictionary<string, bool> Body) Ready(ReadinessState state);   // 테스트용
}
```

`RobotFleet.Hosting.csproj`는 `Microsoft.AspNetCore.App` 프레임워크 참조(`<FrameworkReference Include="Microsoft.AspNetCore.App" />`)로 `IEndpointRouteBuilder`를 쓴다.

**Program 구성**

| 호스트 | 구성 |
|---|---|
| IngestApi | `WebApplication.CreateBuilder(args)` → `builder.Configuration.AddFleetSources(builder.Environment.ContentRootPath, args)` → `AddFleetIngest` → `app.MapPost("/api/v1/events/batch", IngestEndpoints.HandleAsync)` · `MapFleetHealth()` · `MapMetrics()`. 요청 본문 상한 8MB(Kestrel `MaxRequestBodySize`) |
| `IngestEndpoints.HandleAsync(HttpContext ctx, IngestService svc)` | 본문을 바이트로 읽어 `svc.HandleAsync` → `RetryAfterSeconds`가 있으면 `Retry-After` 헤더 → `Results.Json(resp.Body, statusCode: resp.StatusCode)` (camelCase). 본문 읽기 실패 → 400 |
| EventWorker | `WebApplication.CreateBuilder(args)` → `AddFleetSources` → `AddFleetWorker` → Kestrel은 `Fleet:Management:Port`(기본 8081)만 listen → `MapFleetHealth()` · `MapMetrics()`. 업무 엔드포인트 없음(10 D5) |
| Simulator (`--robots N`이 있을 때 fleet 모드) | 맵 로드 → `--ingest`가 있으면 `HttpClient{BaseAddress}` → `HttpIngestClient` → (`--sendlog`면 `SendLog(dir, mapId, runId)`, runId = `RunIds.New(UTC 지금, robots)` → `{yyyyMMdd}-{HHmmss}-r{robots}`, 맵 해시 = `RunIds.MapHash(맵 파일 바이트)`) → `EventBatchSender` → 송신 루프를 `Task.Run(sender.RunAsync)` → `FleetSimulation(map, opts, sink: sender, …).RunAsync` → 끝나면 `sender.DrainAsync` → `unsent` 출력. `--ingest`가 없으면 `sink: null`. `--metrics-port`면 `Prometheus.MetricServer(port).Start()`. 종료 코드 = `FleetRunResult.ExitCode`, 단 `unsent > 0`이면 5. `--robots`가 없으면 기존 M1 경로 그대로 |
| Ctrl+C | 기존 M1과 같이 취소 토큰 → 정상 종료 경로(Drain 포함) |

`appsettings.json` (IngestApi · EventWorker 공통 골격, 값은 비움):

```json
{
  "ConnectionStrings": { "RabbitMq": "", "Redis": "", "Postgres": "" },
  "Fleet": { "Topology": { "Prefix": "" }, "Redis": { "KeyPrefix": "" }, "Worker": { "Id": "" }, "Management": { "Port": 8081 } },
  "Logging": { "LogLevel": { "Default": "Information" } }
}
```

두 host csproj에 `<Content Include="appsettings.Local.json" Condition="Exists('appsettings.Local.json')" CopyToOutputDirectory="PreserveNewest" />`. `appsettings.Local.json` 자체는 만들지 않는다.

**Simulator 옵션 추가** (`SimulationOptions`에 속성 추가, 사용법 문자열 갱신)

| 옵션 | 속성 | 기본 | 검증 |
|---|---|---|---|
| `--robots N` | `int? Robots` | null(=M1 모드) | ≥ 1 |
| `--lambda x` | `double Lambda` | 0.5 | ≥ 0 |
| `--seed n` | `int Seed` | 1 | 정수 |
| `--ingest url` | `Uri? IngestUrl` | null | 절대 http/https URI |
| `--sendlog dir` | `string? SendLogDirectory` | null | |
| `--metrics-port p` | `int? MetricsPort` | null | 1~65535 |

fleet 모드에서 `--start` · `--goal` · `--heading`은 무시(파싱은 기존대로). `FleetOptions`는 `Robots · Lambda · Seed · TickMs · SpeedFactor · MaxTicks`를 옮겨 만든다.

**테스트**

| ID | 내용 |
|---|---|
| H-01 | `AddFleetSources` — 임시 폴더의 `appsettings.json`(빈 값) < `appsettings.Local.json` < 환경 변수(`ConnectionStrings__Postgres`) 우선순위, Local 파일 없으면 오류 없음, `GetConnection` 빈 문자열 → null |
| H-02 | `AddFleetIngest` — RabbitMq 비면 `InvalidOperationException`, Redis 비면 `IIdemStore` · `IPartitionRoster` 미등록 |
| H-03 | `AddFleetWorker` — `IEnumerable<IStartupStep>` 순서가 `postgres.migrations` → `rabbitmq.topology` (자리표시 연결 문자열 `Host=localhost;Database=x` · `amqp://localhost`로 컨테이너만 만들고 연결하지 않는다) |
| H-04 | `HealthEndpoints.Ready` — 요구 구성 요소 하나라도 false면 503 + 스냅숏, 전부 true면 200 |
| H-05 | `SimulationOptionsTests` — 새 옵션 6개 정상 · 오류 행, `--robots` 없으면 `Robots == null` |

**완료 조건**

| 조건 |
|---|
| `dotnet build RobotFleet.sln` 경고 0 (껍데기 상태에서) |
| `dotnet test` — 기존 + H-01~H-05 통과. [핵심] 껍데기를 실행하는 테스트는 만들지 않는다 |
| `dotnet format --verify-no-changes` 통과 |
| 저장소에 접속 정보 값 없음, `appsettings.Local.json` 미생성 |

### [단순 S4] 테스트 장치 · 단순 fake · Reconcile 껍데기

> **D7로 fake 4종(`src/Infrastructure.InMemory`)은 삭제됐다.** 아래는 남은 TestSupport · Reconcile 명세다.

**만들 파일**

| 파일 | 내용 |
|---|---|
| `tests/TestSupport/RobotFleet.TestSupport.csproj` | 클래스 라이브러리. xunit(`FactAttribute`용) · `StackExchange.Redis` · `Npgsql` · `Microsoft.Extensions.TimeProvider.Testing` · `Microsoft.Extensions.Configuration.Json` 8.0.1, Shared 참조 |
| `tests/appsettings.Test.json` | `{ "FLEET_TEST_RABBITMQ": "", "FLEET_TEST_REDIS": "", "FLEET_TEST_PG": "", "FLEET_TEST_RABBITMQ_MGMT": "" }` — 키만. 실제 값 파일 `appsettings.Test.Local.json`은 만들지 않는다(사용자 몫) |
| `tests/TestSupport/{IntegrationFactAttribute,TestEnv,TestRun,StaleResourceSweeper}.cs` | 아래 |
| `src/Reconcile/RobotFleet.Reconcile.csproj` | Exe. `Npgsql` · `Microsoft.Extensions.Configuration.Json` 8.0.1 · `Microsoft.Extensions.Configuration.EnvironmentVariables` 8.0.0, Shared 참조. `appsettings.json` 출력 복사, `appsettings.Local.json`은 있을 때만 복사 |
| `src/Reconcile/appsettings.json` | `{ "ConnectionStrings": { "Postgres": "" } }` |
| `src/Reconcile/{Program,ReconcileOptions,SendLogReader,SentEventImporter,ReconcileReport,IReconcileQueries,ReconcileRunner}.cs` | 아래 |
| `src/Reconcile/ReconcileQueries.cs` | **자리표시만**: `IReconcileQueries` 구현, 모든 메서드 `throw new NotImplementedException()`. 본문은 2차 [핵심]이 채운다 |
| `tests/Reconcile.Tests/RobotFleet.Reconcile.Tests.csproj`, `SendLogReaderTests.cs`, `ReconcileOptionsTests.cs`, `TestRunNameTests.cs`, `TestEnvTests.cs` | K-01~K-04. Reconcile · TestSupport 참조 |

```csharp
namespace RobotFleet.TestSupport;

/// 지정한 설정 이름 중 하나라도 TestEnv.Get이 null이면
/// Skip = "{이름} 없음 (환경 변수 또는 tests/appsettings.Test.Local.json)". 클래스에는 [Trait("Category","Integration")]를 따로 단다
public sealed class IntegrationFactAttribute : FactAttribute { public IntegrationFactAttribute(params string[] settingNames); }
public sealed class IntegrationTheoryAttribute : TheoryAttribute { public IntegrationTheoryAttribute(params string[] settingNames); }

public static class TestEnv
{
    public const string RabbitMqVar = "FLEET_TEST_RABBITMQ", RedisVar = "FLEET_TEST_REDIS",
                        PgVar = "FLEET_TEST_PG", RabbitMgmtVar = "FLEET_TEST_RABBITMQ_MGMT";

    /// 환경 변수 > tests/appsettings.Test.Local.json > tests/appsettings.Test.json. 빈 문자열 · 공백은 null.
    /// 파일은 프로세스당 한 번 읽어 캐시한다
    public static string? Get(string name);
    public static string? RabbitMq => Get(RabbitMqVar);   public static string? Redis => Get(RedisVar);
    public static string? Pg => Get(PgVar);               public static string? RabbitMgmt => Get(RabbitMgmtVar);
    /// RobotFleet.sln이 있는 폴더 아래 tests/. 못 찾으면 null(파일 출처 없이 환경 변수만)
    public static string? TestsDirectory { get; }

    /// 테스트용 순수 함수: 위 우선순위를 주입된 출처로 계산
    internal static string? Resolve(string name, Func<string, string?> environment, string? testsDirectory);
}

public sealed class TestRun
{
    public string RunId { get; }              // "t" + yyyyMMddHHmmss(UTC) + 소문자 hex 4자리
    public string RabbitPrefix => RunId + ".";
    public string RedisPrefix => RunId + ":";
    public string PgSchema => "t_" + RunId;
    public static TestRun New(TimeProvider? time = null);
    public static bool TryParseCreatedAt(string resourceName, out DateTimeOffset createdAt);   // 접두사 어디서든 runId 추출
    public static bool IsStale(string resourceName, DateTimeOffset now, TimeSpan age);         // 기본 1시간
}

public static class StaleResourceSweeper
{
    public static Task<int> SweepRedisAsync(IConnectionMultiplexer redis, DateTimeOffset now, CancellationToken ct);   // SCAN "t*:*"
    public static Task<int> SweepPostgresAsync(NpgsqlDataSource pg, DateTimeOffset now, CancellationToken ct);         // 스키마 "t_t*"
    /// FLEET_TEST_RABBITMQ_MGMT(관리 API URL)가 있을 때만. 없으면 0 반환하고 아무것도 안 한다
    public static Task<int> SweepRabbitAsync(string? managementUrl, DateTimeOffset now, CancellationToken ct);
}
```

```csharp
namespace RobotFleet.Reconcile;

/// --sendlog <파일|디렉터리> --map <mapId> [--out <json>] [--drain-seconds 10].
/// 연결 문자열은 ConnectionStrings:Postgres — appsettings.json → appsettings.Local.json → 환경 변수(§9 순서)
public sealed record ReconcileOptions(string SendLogPath, string MapId, string? OutPath, int DrainSeconds)
{ public static ReconcileOptions Parse(string[] args); }   // 잘못된 인자 → ArgumentException

public sealed record SentEvent(string EventId, string BatchId, long Tick, string EventType, string Category, string Key);
public static class SendLogReader { public static IEnumerable<SentEvent> Read(string path); }   // §5 JSONL, 디렉터리면 *.jsonl 전부
public static class SentEventImporter
{
    /// 임시 테이블 sent(event_id, batch_id, tick, event_type, category, key)에 binary COPY. 반환 = 행 수
    public static Task<long> ImportAsync(NpgsqlConnection conn, IEnumerable<SentEvent> events, CancellationToken ct);
}
public sealed record CategoryCount(long Sent, long Stored, long Missing, IReadOnlyList<string> MissingSample);
public sealed record ReconcileReport(string MapId, CategoryCount Task, CategoryCount Conflict,
    CategoryCount Telemetry, long InvalidTransitions);   // 종료 코드: Task · Conflict Missing 0 → 0
public interface IReconcileQueries
{
    Task<CategoryCount> TaskAsync(NpgsqlConnection conn, string mapId, CancellationToken ct);
    Task<CategoryCount> ConflictAsync(NpgsqlConnection conn, string mapId, CancellationToken ct);
    Task<CategoryCount> TelemetryAsync(NpgsqlConnection conn, string mapId, CancellationToken ct);
    Task<long> InvalidTransitionsAsync(NpgsqlConnection conn, string mapId, CancellationToken ct);
}
```

```csharp
public sealed class ReconcileRunner(IReconcileQueries queries, TextWriter output)
{
    /// 적체 0 대기는 하지 않는다(실행 스크립트가 맡는다). 연결 → SentEventImporter → 질의 4개 → 보고서 JSON
    /// (System.Text.Json 기본 + camelCase) 출력 · --out 파일 저장 → 종료 코드 반환
    public Task<int> RunAsync(ReconcileOptions options, string connectionString, CancellationToken ct);
}
```

`Program`은 `ReconcileOptions.Parse` → 구성 읽기(§9 순서: `appsettings.json` → `appsettings.Local.json`(optional) → 환경 변수) → `ConnectionStrings:Postgres`가 비면 "appsettings.Local.json 또는 ConnectionStrings__Postgres 필요" 메시지 · 종료 코드 2 → `new ReconcileRunner(new ReconcileQueries(), Console.Out).RunAsync(...)`.

| ID | 테스트 |
|---|---|
| K-01 | `SendLogReader` — §5 행 형식 3줄 파일 → 3개, 빈 줄 무시, 깨진 JSON 줄 → 줄 번호 포함 예외 |
| K-02 | `ReconcileOptions.Parse` — 필수 누락 · 모르는 옵션 · 기본값 |
| K-04 | `TestEnv.Resolve` — 환경 변수가 Local 파일을 이김, Local이 Test.json을 이김, 빈 문자열 → null, 폴더 없음 → 환경 변수만, Local 파일 없음 → 오류 없이 Test.json (임시 폴더에 파일을 만들어 검증) |

**완료 조건**

| 조건 |
|---|
| 세 csproj와 `tests/Reconcile.Tests` 빌드 경고 0 (S1 시그니처 기준. S1 병합 전이면 중간 관리자가 병합 후 확인) |
| K-01~K-04 통과 |
| K-03 `tests/Reconcile.Tests/TestRunNameTests.cs` — `TestRun.New` 형식(`^t\d{14}[0-9a-f]{4}$`), `TryParseCreatedAt`이 접두사 붙은 이름(`t20261012093000ab12.fleet.task.p00`, `t_t20261012093000ab12`, `t20261012093000ab12:idem:x`)에서 시각 추출, `IsStale` 경계 |

### [핵심] 중간 관리자

업무 로직은 호스트 없는 라이브러리에 둔다(S3와 파일이 겹치지 않게). 껍데기는 S3 착수 전에 커밋했다.

| 프로젝트 | 파일 |
|---|---|
| `src/Simulator` | `Fleet/{FleetOptions,FleetSimulation,TaskGenerator,TaskDispatcher,TaskLifecycle}.cs`, `Events/{EventCollector,EventBatchSender,EventSenderOptions,SendLog,HttpIngestClient,DelegateIngestClient}.cs`, `RobotStepper` 관찰 지점 |
| `src/Ingest.Core` | `IngestService` · `IngestResponse` · `IngestStartupService` · 메트릭 · 등록 확장 |
| `src/EventWorker.Core` | `WorkerService` · `AssignmentCoordinator` · `PartitionProcessor` · `EventHandlers` · 메트릭 · 등록 확장 |
| `src/Infrastructure.RabbitMq` | `RabbitConnectionProvider` · `TopologyDeclarer` · `RabbitEventPublisher` · `RabbitPartitionSubscriber` · 등록 확장 |
| `src/Infrastructure.Redis` | `RedisConnectionProvider` · `RedisIdemStore`(+ Lua) · `RedisPartitionRoster` · 등록 확장(미설정이면 미등록) |
| `src/Infrastructure.Postgres` | `PgEventStore` · `PostgresStartupStep` · `PostgresOptions` · 등록 확장 |
| `src/Reconcile` | `ReconcileQueries.cs` |
| `src/Shared` | `Pipeline/{IStartupStep,ReadinessState}.cs`, `TopologySpec`의 DLX → DLQ 바인딩(49개) |
| `tests/Pipeline.Tests` (신규) | 순수 로직: I-16 · 메시지 생성 · UUIDv7 |
| `tests/Infrastructure.Tests` | 통합: C · T · D · I · W · R · S · E2E (§12), 계약 추상 클래스, `PipelineRig` |
| `tests/Simulator.Tests` | G · E · S 시리즈 |

- [ ] 생애주기 · 생성기 · 할당기 + G-01~G-06
- [ ] 수집기 · 송신기 · 송신 로그 + E-01~E-04 · S-01~S-07
- [ ] Ingest + Redis 멱등 Lua + I-01~I-16
- [ ] RabbitMQ 선언 · 확인 추적 · 구독
- [ ] 워커 처리기 · 핸들러 · 배정 · Redis 명부 + W-01~W-12 · R-01~R-07
- [ ] PgEventStore + D-04
- [ ] 계약 + C-01~C-04 · T-01~T-03
- [ ] Reconcile 차집합 SQL
- [ ] 통합 테스트(§12), S3 병합 후 통합 확인, X-01~X-04 보고서(실제 서버 준비 후)

---

## 14. 문서 · README 충돌 지점

| 위치 | 현재 | 수정 |
|---|---|---|
| README 기술 스택 | `RabbitMQ (MassTransit)` | `RabbitMQ (RabbitMQ.Client)` (D1) |
| 01 EventWorker 절 | "실패 시 재시도 → 초과하면 DLQ" | 한 줄 보강: DB 일시 오류는 ack 없이 재시도, 독 메시지 · 전달 횟수 초과만 DLQ → 10 §6 |
| 01 워커 2차 방어 `metrics_minute` 행 | `metrics_applied` Redis Set | `ON CONFLICT … RETURNING`으로 새로 들어간 행만 집계 (D4) → 10 §6 |
| 01 PostgreSQL 테이블 | 영수증 · 상태 테이블 없음 | "추가 테이블은 10 §8" 한 줄 |
| 02 | 충돌 없음 | 수정하지 않는다 |
