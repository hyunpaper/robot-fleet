# 10. M3 상세 설계 — 이벤트 파이프라인

09의 큰 틀 아래 위임 항목 1~14를 채운다. 값마다 근거를 한 줄로 붙이고, 측정으로 확정할 값은 **M3 측정**으로 표시한다.

---

## 09 대비 변경점

| # | 09 원문 | 10 결정 | 근거 |
|---|---|---|---|
| D1 | 메시징 라이브러리 MassTransit 기본 | **RabbitMQ.Client 7 직접 사용** | §3 항목별 표. 01 토폴로지 7개 중 4개가 MassTransit 기본 동작과 정면으로 부딪친다(봉투 · 오류 큐 · 처리 순서 ack · 반환 의미론) |
| D2 | 인프라 인터페이스 5종 | **6종** — `IPartitionSubscriber`(소비 쪽) 추가 | `IEventPublisher`는 발행만 다룬다. 워커의 구독 · SAC · ack · 반환을 fake로 바꾸려면 소비 쪽 경계가 따로 있어야 한다 |
| D3 | 파티션 생성은 기동 시와 자정 | 기동 시 + **매시 정각**, 그리고 **DEFAULT 파티션** | 자정 타이머 하나는 그 순간 재시작 중이면 놓친다. 매시 확인은 멱등이라 비용이 없다. DEFAULT는 그래도 빠진 날짜의 행을 받아 INSERT 실패(=유실)를 원천 차단 |
| D4 | (01) `metrics_minute` 멱등은 Redis `metrics_applied` 집합 | **같은 PG 트랜잭션의 `INSERT … ON CONFLICT DO NOTHING RETURNING`** 결과로만 집계 | M3의 Redis는 선택이다(09). 실제로 새로 들어간 행만 집계하면 별도 집합 없이 정확히 1회. 집합 기록과 집계가 같은 트랜잭션이라 사이에서 죽어도 어긋나지 않는다 |
| D5 | (09) EventWorker는 HTTP 없음 | 업무 API는 없고 **관리 포트**(`/metrics` · `/health/*`)만 | 09 완료 기준 2~6의 메트릭 출처이고 readiness가 필요하다 |
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
| `TaskGenerator` | 〃 | 포아송(λ건/초, 틱당 `λ × tickMs/1000` 기대값), Station 무작위 출발 · 목적(같으면 재추첨). **시드 고정 가능**(재현 실험) |
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
  "tick": 142057,
  "occurredAt": "2026-04-01T09:14:23.412Z",
  "events": [ { "eventType": "RobotTelemetry", "payload": { } } ]
}
```

큐 메시지 본문 = 항목 하나의 공통 봉투(03)에 `schemaVersion`을 더한 것. `eventId`는 Ingest가 `{batchId}/{index}`로 채운다.

| 항목 | 결정 | 근거 |
|---|---|---|
| 타입 | `EventBatch`, `EventEnvelope`, 이벤트별 `sealed record` 페이로드 9종, `EventType` 상수, `EventCategory { Telemetry, Task, Conflict }` | 03 스키마 그대로 |
| 다형성 | `eventType` 문자열로 판별, 페이로드는 `JsonElement`로 받아 타입별 역직렬화 | `$type` 같은 라이브러리 판별자를 넣지 않는다 — 03 JSON 그대로 |
| 직렬화 | `System.Text.Json` **소스 생성기**(`FleetJsonContext`), camelCase, 숫자 문자열 금지, `null` 생략 | 리플렉션 없음 · 빠름. 06 원칙(직렬화 가능한 것만) |
| 필드 순서 | 선언 순서 고정 + `[JsonPropertyOrder]` | 송신 로그 · 해시 · 대조가 바이트 단위로 같아야 한다 |
| `schemaVersion` | 정수 1. Ingest는 모르는 버전 → 400, 워커는 → `fleet.invalid` | 바뀌는 날 양쪽이 같이 판단한다 |
| 라우팅 키 | `RoutingKeys.Of(envelope)` 순수 함수 — telemetry → `robotId`, Task* → `taskId`, RobotStatusChanged → `robotId`, conflict 범주 → `mapId` | 01 표. 한 곳에서만 정한다 |
| 범주 · 지속성 | `EventCategories.Of(eventType)` — 03 "이벤트 → 큐 매핑" 표 그대로. PathReplanned는 conflict 범주(08 §8) | |
| 메시지 속성 | `MessageId = eventId`, `Type = eventType`, `ContentType = application/json`, `DeliveryMode` = 범주별, 헤더 `x-schema-version` | 워커가 본문을 열기 전에 분기 가능 |
| 토폴로지 명세 | `TopologySpec` — exchange · 큐 · 인자 · 바인딩을 **데이터로** 정의. 선언 실행은 인프라 프로젝트 | Shared를 외부 패키지 없이 유지(09 "Shared.Messaging에 토폴로지 선언"을 데이터로 해석) |
| 인터페이스 6종 | `IEventSink` · `IEventPublisher` · `IIdemStore` · `IEventStore` · `IPartitionRoster` · `IPartitionSubscriber` 를 `src/Shared/Pipeline/` 에 | 인프라 · fake · 서비스가 순환 없이 참조 |

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
| 순서 | exchange 4(`fleet.telemetry` · `fleet.task` · `fleet.conflict` · `fleet.dlx`) → 큐 50(파티션 48 + `fleet.dlq` + `fleet.invalid`) → 바인딩 48(가중치 `"1"`) | 바인딩 전에 양끝이 있어야 한다 |
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
| 1 | 스키마 검증: `schemaVersion == 1`, `batchId` UUIDv7, `events` 1~1000, `eventType` 알려진 값, 페이로드 필수 필드 · 라우팅 키 필드 존재 | **400** + 첫 오류 위치. 재시도하지 않을 오류 |
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
| 행 | 이벤트당 1행 `{"eventId","batchId","tick","eventType","category","key"}` — `key`는 라우팅 키(telemetry 대조용 `robotId`) |
| 근거 | 202 이후 = Ingest가 책임을 넘겨받은 집합. 202 전 버퍼분은 종료 시 `unsent`로 따로 센다 |

`IIngestClient` 구현 2개: `HttpIngestClient`(원격) · `InProcessIngestClient`(같은 프로세스의 `IngestService` 직접 호출, §9).

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
| 상태 전이 위반 | `fleet.invalid`로 발행(확인 대기) 후 정상 처리로 보고 ack | 03. 순서 이상 탐지 장치 |
| 프로세스가 죽음 | 미확인분은 브로커가 반환 → 다음 활성 소비자가 재처리 → 중복은 테이블이 막는다 | 반복 크래시는 `x-delivery-limit 20` → DLQ |

### 이벤트별 저장

| 이벤트 | 테이블 · 방식 | 멱등 |
|---|---|---|
| RobotTelemetry | `robot_telemetry` `unnest` INSERT `ON CONFLICT DO NOTHING RETURNING robot_id, status, occurred_at` | 자연 키(01) |
| (집계) | 위 RETURNING 행만 `metrics_minute`에 상태별 `tickMs` 가산 (Moving → move, Loading · Unloading → work, Waiting → wait, Idle → idle) | **새로 들어간 행만** 집계(D4) |
| Task* | `task_event_log` INSERT `ON CONFLICT (event_id) DO NOTHING RETURNING` → 새 행만 `task_history` 단조 upsert(01) | 이벤트 영수증 + 단조 상태 |
| (집계) | 새로 들어간 TaskCompleted만 `metrics_minute.task_count + 1` | 위와 같은 방식 |
| RobotStatusChanged | 검증 → `robot_status_log` INSERT `ON CONFLICT DO NOTHING RETURNING` → 새 행만 `robot_state` 갱신 | |
| ConflictDetected · DeadlockDetected | `conflict_log` `ON CONFLICT (event_id) DO NOTHING` | 01 |
| PathReplanned | M3는 `conflict_log`에 넣지 않고 세기만(`fleet_worker_messages_total`) | 테이블이 01에 없다. M7에서 정한다 |

`tickMs`는 워커 설정 `Fleet:TickMs`(기본 100). Simulator `--tick`과 같아야 한다 — 다르면 시간 분해가 틀린다(실행 스크립트가 같은 값을 넘긴다).

### 상태 전이 검증

| 항목 | 결정 | 근거 |
|---|---|---|
| 기준 상태 | `robot_state(map_id, robot_id, status, last_tick, last_event_id)` | 재배정으로 파티션 주인이 바뀌어도 이어서 검증 |
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
| `robot_telemetry` (일 파티션) + `robot_telemetry_default` | `(map_id, robot_id, tick, occurred_at)` | DEFAULT는 D3 |
| `task_history` · `conflict_log` · `metrics_minute` | 01 | |
| `task_event_log(event_id PK, map_id, task_id, event_type, tick, occurred_at)` | `event_id` | D6 |
| `robot_status_log(event_id PK, map_id, robot_id, tick, from_status, to_status, reason, occurred_at)` | `event_id` | D6 · 중복 판별 |
| `robot_state(map_id, robot_id PK, status, last_tick, last_event_id)` | `(map_id, robot_id)` | §6 검증 |
| `schema_migrations` | `version` | |

### 파티션

| 항목 | 결정 | 근거 |
|---|---|---|
| 함수 | `fleet_ensure_telemetry_partition(day date)` — `CREATE TABLE IF NOT EXISTS robot_telemetry_YYYYMMDD PARTITION OF …` | 멱등 |
| 호출 | 마이그레이션 직후(당일 · 익일, **UTC**) + 매시 정각 | D3. `occurred_at`이 UTC라 날짜 경계도 UTC |
| DEFAULT에 행이 생김 | 메트릭 `fleet_telemetry_default_rows`(매시 `count(*)`), 0이 아니면 경보 | 시계 오차 신호. 그 날짜 파티션 생성이 DEFAULT 행과 겹쳐 실패할 수 있으므로 사람이 옮긴다 |

### 배치 INSERT

```sql
INSERT INTO robot_telemetry (map_id, robot_id, tick, x, y, heading, status, battery, occurred_at)
SELECT * FROM unnest(@map_id::text[], @robot_id::text[], @tick::bigint[], @x::smallint[], @y::smallint[],
                     @heading::smallint[], @status::smallint[], @battery::smallint[], @occurred_at::timestamptz[])
ON CONFLICT DO NOTHING
RETURNING robot_id, status, occurred_at;
```

| 설정 | 값 | 근거 |
|---|---|---|
| 방식 | `unnest` 배열 파라미터 | 행 수와 무관하게 파라미터 9개. 다중 VALUES는 65535 파라미터 한계와 매번 다른 SQL 문 |
| Npgsql | 8.x, `NpgsqlDataSource` | |
| `MaxPoolSize` | **10** / 워커 | 워커 최대 8대(HPA) × 10 = 80 < PostgreSQL 기본 `max_connections` 100. 파티션 처리기는 연결 세마포어로 대기 |
| 명령 타임아웃 | 10s | 배치는 수 ms. 넘으면 일시 오류로 보고 재시도 |

---

## 9. 실행 구성

모든 진입점이 같은 조립 함수(`services.AddFleetIngest(config)`, `AddFleetWorker`, `AddFleetSimulatorEvents`)를 쓰고, 구현 선택은 설정으로만 갈린다.

| 모드 | 진입점 | 구성 |
|---|---|---|
| **in-process** | `src/PipelineHost` | Simulator(`FleetSimulation`) + `InProcessIngestClient` → `IngestService` → `InMemoryBroker` → `WorkerService` → `InMemoryEventStore`. 인프라 없음. 종료 시 대조 결과 출력 |
| **원격 인프라** | Simulator · IngestApi · EventWorker 각 프로세스 | 실제 RabbitMQ · Redis · PostgreSQL |
| (M5) k3s | 같은 이미지 | 같은 설정 키, ConfigMap · Secret |

| 설정 키 | 의미 | 없을 때 |
|---|---|---|
| `ConnectionStrings:RabbitMq` | AMQP URI | IngestApi · EventWorker 기동 실패 |
| `ConnectionStrings:Redis` | StackExchange.Redis 구성 문자열 | fail-open · 단일 워커 |
| `ConnectionStrings:Postgres` | Npgsql 연결 문자열 | EventWorker 기동 실패 |
| `Fleet:TickMs` | 시간 분해 기준 | 100 |
| `Fleet:Topology:Prefix` | 이름 접두사 | 빈 값 |

환경 변수는 .NET 규칙(`ConnectionStrings__RabbitMq`). **저장소에는 키 이름만 있고 값은 없다.** `appsettings*.json`에도 연결 문자열을 넣지 않는다.

Simulator 명령행 추가: `--robots N` · `--lambda λ` · `--seed n` · `--ingest <url>` · `--sendlog <dir>` · `--metrics-port p`. `--ingest`가 없으면 이벤트를 만들지 않는다(M1 동작 유지).

---

## 10. 유실 0 대조 도구

`tools/Reconcile` (콘솔). M8 §12와 공용.

| 단계 | 동작 |
|---|---|
| 1 | 적체 0 대기: 파티션 큐 메시지 수 합 = 0이 10초 유지(관리 API 대신 `QueueDeclarePassive` 메시지 수) |
| 2 | 송신 로그를 임시 테이블 `sent(event_id, event_type, category, key, tick)`로 `COPY`(Npgsql binary import) |
| 3 | 차집합 SQL (아래) |
| 4 | JSON 요약 출력 + 종료 코드: task · conflict 누락 0이면 0, 아니면 1 |

```sql
-- task 범주: Task* + RobotStatusChanged
SELECT s.event_id FROM sent s
WHERE s.category = 'task'
  AND NOT EXISTS (SELECT 1 FROM task_event_log t WHERE t.event_id = s.event_id)
  AND NOT EXISTS (SELECT 1 FROM robot_status_log r WHERE r.event_id = s.event_id);
-- conflict 범주 (PathReplanned 제외)
SELECT s.event_id FROM sent s
WHERE s.category = 'conflict' AND s.event_type <> 'PathReplanned'
  AND NOT EXISTS (SELECT 1 FROM conflict_log c WHERE c.event_id = s.event_id);
-- telemetry: 누락 수만
SELECT count(*) FROM sent s
WHERE s.category = 'telemetry'
  AND NOT EXISTS (SELECT 1 FROM robot_telemetry r WHERE r.robot_id = s.key AND r.tick = s.tick);
```

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

### 공통 장치

| 장치 | 결정 | 근거 |
|---|---|---|
| L2 skip | `[IntegrationFact("FLEET_TEST_RABBITMQ")]` — 생성자에서 환경 변수가 비면 `Skip = "FLEET_TEST_RABBITMQ 없음"`. `[Trait("Category","Integration")]` | 추가 패키지 없이 xUnit 2.9에서 **skip으로 표시**된다(실패 아님) |
| 환경 변수 | `FLEET_TEST_RABBITMQ`(AMQP URI) · `FLEET_TEST_REDIS` · `FLEET_TEST_PG` | 09. 값은 저장소 밖(사용자 셸 · CI 비밀) |
| 실행 id | `t{yyyyMMddHHmmss}{4자리 난수}` | 공용 서버를 여러 실행이 나눠 쓴다 |
| 격리 | RabbitMQ: 토폴로지 접두사 `{runId}.` / Redis: 키 접두사 `{runId}:` / PostgreSQL: 스키마 `t_{runId}` + `search_path` | vhost 생성은 관리자 권한이 필요하다. 접두사는 일반 권한으로 된다 |
| 정리 | 테스트 클래스 `IAsyncLifetime.DisposeAsync`에서 삭제. 시작 시 **1시간 지난 `t…` 자원 청소** | 중간에 죽은 실행의 잔여물이 다음 실행을 오염시키지 않게 |
| 계약 테스트 | 추상 클래스 `XxxContract`(테스트 메서드 보유) ← `InMemoryXxxTests`(L1) · `RedisXxxTests`/`RabbitXxxTests`/`PgXxxTests`(L2) | fake와 실제가 같은 표를 통과해야 한다(09) |
| 시계 | `TimeProvider` 주입(`FakeTimeProvider`) | 5초 고아 · 30초 만료 · 배치 창을 즉시 검증 |

### fake 6종과 실패 주입

| fake | 계약 상대 | 실패 주입 |
|---|---|---|
| `InMemoryEventSink` | `EventCollector` 출력 검사용 | — |
| `InMemoryIdemStore` | `RedisIdemStore` | 다음 N회 예외(=Redis 없음), 시계 이동 |
| `InMemoryBroker` (`IEventPublisher` + `IPartitionSubscriber`) | `RabbitEventPublisher` · `RabbitPartitionSubscriber` | confirm nack · confirm 지연(타임아웃) · 큐 가득(`reject-publish`) · 소비자 강제 종료(미확인 반환 + 전달 횟수) · 중복 전달 · `x-delivery-limit` → DLQ |
| `InMemoryEventStore` | `PgEventStore` | 다음 N회 일시 오류 · 커밋 직후 예외(커밋됐는데 실패로 보임) |
| `InMemoryPartitionRoster` | `RedisPartitionRoster` | heartbeat 정지(만료) · Redis 끊김 |
| `FakeIngestClient` | `HttpIngestClient` | 503 · 타임아웃 · 400 순서 지정 |

> `InMemoryBroker`는 consistent-hash(키 해시 → 파티션), SAC(먼저 붙은 소비자만 활성, 취소 시 다음으로), prefetch,
> `multiple` ack, 채널 닫기 반환을 **실제 의미론대로** 구현한다. 이 fake가 틀리면 L1 전체가 거짓 안심이 된다 — 그래서 `[핵심]`이다.

### L1 단위 (인프라 없음, 항상)

| ID | 대상 | 검증 |
|---|---|---|
| G-01 | 생성기 | 같은 시드 → 같은 작업 열. 출발 = 목적이면 재추첨 |
| G-02 | 생성기 | λ=5, 10만 틱 평균이 λ의 ±3% |
| G-03 | 할당기 | 최근접 Idle, 동점 `robotId` 순 |
| G-04 | 생애주기 | `Idle → Moving → Loading → Moving → Unloading → Idle` 전이 · 사유 순서 |
| G-05 | 생애주기 | 출발지에서 할당 → 같은 틱 `Idle → Moving → Loading` |
| G-06 | 생애주기 | 경로 없음 → `Moving → Error(no_path)` + TaskFailed |
| E-01 | 수집기 | 배치 안 순서(§1 표) — Created → 로봇별(Conflict → Status → Task) → telemetry |
| E-02 | 수집기 | 대기 구간 1개 → ConflictDetected 1건, `waitMs` = 구간 길이 |
| E-03 | 수집기 | 1000건 초과 → 두 배치, 순서 유지, 두 번째 `batchId` > 첫 번째 |
| E-04 | 수집기 | 모든 RobotStatusChanged가 `StatusTransitions` 표 안 |
| M-01 | 계약 | 03 예시 JSON 왕복 직렬화 바이트 동일 |
| M-02 | 계약 | 라우팅 키 — 범주 · 이벤트별 표 전부 |
| M-03 | 계약 | `eventId = {batchId}/{index}` |
| M-04 | 계약 | `TopologySpec` 인자가 08 §8 표와 같음 |
| I-01~I-07 | Ingest 멱등 | **08 §9 상태 전이 표 7행을 행마다 1개**: 신규 BEGIN · done 중복 202 · pending 5s 미만 503 · 고아 TAKEOVER · 전부 확인 → done · task 실패 → 내 pending만 삭제 → 503 · Redis 오류 → 건너뜀 → 202 |
| I-08 | Ingest 멱등 | 고아 재점유 후 원래 인스턴스의 `Abort`는 남의 pending을 지우지 않는다 |
| I-09 | 발행 | telemetry nack → 202, `telemetryDropped` 증가 |
| I-10 | 발행 | telemetry 200ms 타임아웃 → 202 |
| I-11 | 발행 | **task confirm nack → 503**, 키 삭제, 재전송 → 202 |
| I-12 | 발행 | conflict confirm 2s 타임아웃 → 503 |
| I-13 | 발행 | 채널 끊김 중간 → 503, 재전송 시 앞부분 중복은 eventId 동일 |
| I-14 | 발행 | 큐 가득(`reject-publish`) → 503 |
| I-15 | 발행 | 큐에 들어간 순서 = `events` 배열 순서(같은 라우팅 키 기준) |
| I-16 | 검증 | 400 사유 표(버전 · UUIDv7 · 개수 · 모르는 타입 · 필수 필드) |
| S-01 | 송신기 | 순차: 머리 배치 202 전에는 다음 배치를 보내지 않는다 |
| S-02 | 송신기 | 503 · 타임아웃 재시도가 같은 바이트 · 같은 `batchId` |
| S-03 | 송신기 | 백오프 100 → 200 → … → 2000 상한 |
| S-04 | 송신기 | 버퍼 600 가득 → 틱 정지, 비면 재개, `stall` 메트릭 |
| S-05 | 송신기 | 400 → 송신 정지 · 종료 코드 4 · 덤프 |
| S-06 | 송신기 | 202 뒤에만 송신 로그, 종료 시 `unsent` 수 |
| S-07 | 송신기 | 틱 루프 호출이 네트워크를 기다리지 않는다(느린 fake 클라이언트에서도 틱 시간 불변) |
| W-01 | 처리기 | N=50 또는 T=50ms 창 |
| W-02 | 처리기 | 커밋 후 마지막 tag `multiple` ack, 커밋 전 ack 없음 |
| W-03 | 처리기 | **DB 일시 오류 → ack · nack 없이 같은 배치 재시도**, 전달 횟수 불변 |
| W-04 | 처리기 | 커밋 직후 예외(커밋됨) → 재처리 시 중복 0 |
| W-05 | 처리기 | **워커 kill(미확인 반환) → 다른 소비자 재처리 → 행 · 집계 1회** |
| W-06 | 처리기 | 역직렬화 실패 1건 → 그 건만 DLQ, 나머지 정상 |
| W-07 | 처리기 | 같은 메시지 21회 반환 → DLQ (`x-delivery-limit 20`) |
| W-08 | 검증 | 금지 전이 → `fleet.invalid`, ack |
| W-09 | 검증 | 중복 전달된 RobotStatusChanged는 오탐하지 않는다 |
| W-10 | 집계 | telemetry 중복 → `metrics_minute` 1회 가산 |
| W-11 | 집계 | TaskCompleted 중복 → `task_count` 1회 |
| W-12 | 저장 | task_history 단조 — 낮은 상태 늦게 와도 무시 |
| R-01 | 배정 | 2대 → `p % 2 == rank`, 3대 → `p % 3` |
| R-02 | 배정 | 순위 = id 정렬 위치, heartbeat로 바뀌지 않음 |
| R-03 | 배정 | **전환 순서: 새 몫 구독이 옛 몫 취소보다 먼저**, 어느 순간에도 파티션 활성 소비자 ≤ 1 |
| R-04 | 배정 | 재배정 중 같은 로봇 이벤트 순서 유지(재배정 1회 포함 1만 건, invalid 0) |
| R-05 | 배정 | 2회 연속 같은 결과일 때만 적용 |
| R-06 | 배정 | 30초 heartbeat 없으면 제외 → 남은 워커가 몫 확장 |
| R-07 | 배정 | Redis 미설정 → 48큐 전부 / 실행 중 끊김 → 몫 고정 |
| P-01 | in-process | 로봇 20대 · 2000틱 → 차집합 0, invalid 0 |
| P-02 | in-process | P-01 + 워커 kill 2회 + 브로커 nack 주입 → 차집합 0, 중복 반영 0 |

### L2 통합 (원격 서버, 접속 정보 있을 때)

| ID | 대상 | 검증 |
|---|---|---|
| C-01 | `IIdemStore` 계약 | I-01~I-08을 Redis에서 (Lua 원자성 포함) |
| C-02 | `IPartitionRoster` 계약 | R-01 · R-02 · R-06을 Redis에서 |
| C-03 | `IEventStore` 계약 | W-10~W-12 · 중복 · RETURNING을 PostgreSQL에서 |
| C-04 | `IEventPublisher` · `IPartitionSubscriber` 계약 | confirm · SAC 활성 전환 · `multiple` ack · 채널 닫기 반환 · 전달 횟수 증감 여부 |
| T-01 | 토폴로지 | 선언 두 번 → 성공, 인자 다른 기존 큐 → 기동 실패 |
| T-02 | 토폴로지 | consistent-hash: 같은 키 1000건 → 한 큐, 키 1만 개 → 16큐 모두 사용 |
| T-03 | 토폴로지 | `reject-publish` → nack, `x-delivery-limit` 초과 · reject → `fleet.dlq` |
| D-01 | 마이그레이션 | 빈 스키마 → 전부 적용, 재기동 → 아무것도 안 함, 체크섬 변경 → 실패 |
| D-02 | 마이그레이션 | 두 프로세스 동시 기동 → 한 번만 적용 |
| D-03 | 파티션 | 당일 · 익일 생성, 범위 밖 날짜 → DEFAULT, 메트릭 |
| D-04 | 배치 INSERT | 1000행 `unnest` · `RETURNING`이 새 행만 |

### L3 E2E (수동, 보고서 첨부)

| ID | 시나리오 | 완료 기준 |
|---|---|---|
| X-01 | 200대 · 10Hz · 30분, 워커 강제 종료 2회 + Ingest 재시작 1회 → Reconcile | 1 · 2 · 6 |
| X-02 | 워커 2대 기동 → 30분 중 3대째 추가 · 제거(재배정) | 3 |
| X-03 | X-01 동안 큐 적체 · `fleet_event_lag_seconds` p99 | 4 |
| X-04 | 같은 시드로 파이프라인 없이 / 있이 → `fleet_tick_overrun_total` 비교 | 5 |

### 완료 기준 ↔ 증명 대응 (09 승인 조건 1)

| 기준 | 코드 | 테스트 | 스크립트 · 도구 |
|---|---|---|---|
| 1 task · conflict 유실 0 | `IngestService` 확인 판정, `EventBatchSender`, `PartitionProcessor` | I-11~I-14, S-01~S-06, W-03~W-05, P-01 · P-02 | `tools/Reconcile` + X-01 |
| 2 중복 0 | `PgEventStore`(RETURNING 집계), `robot_status_log` | W-04 · W-05 · W-09~W-12, C-03 | 메트릭 `outcome="duplicate"` vs 행 수, X-01 |
| 3 순서 유지 | `AssignmentCoordinator`, SAC 토폴로지, `StatusTransitions` 검증 | R-03 · R-04, W-08, C-04 | `fleet_invalid_depth` · `fleet_worker_messages_total{outcome="invalid"}`, X-02 |
| 4 처리량 · 지연 | 배치 창 · `unnest` · 확인 몰아받기 | W-01, D-04 | `fleet_event_lag_seconds` · `fleet_queue_depth`, X-03 |
| 5 틱 예산 | 버퍼 + 백그라운드 송신 | S-04 · S-07 | `fleet_tick_overrun_total`, X-04 |
| 6 telemetry 유실 계측 | 범주별 확인 타임아웃 | I-09 · I-10 | `fleet_telemetry_dropped_total` + Reconcile telemetry 행 |
| 7 테스트 | `IntegrationFact` · 계약 기반 클래스 · 실행 id 청소 | L1 전부 · L2 전부 | `dotnet test`(서버 없으면 L2 skip) |

### "처음 띄울 때" 경로 (09 승인 조건 5)

| 순서 | 주체 | 동작 | 실패 시 |
|---|---|---|---|
| 1 | EventWorker | PostgreSQL 연결 → advisory lock → 마이그레이션 → 당일 · 익일 파티션 | 기동 실패 (재시작 정책이 재시도) |
| 2 | EventWorker · IngestApi | RabbitMQ 연결 → 토폴로지 선언(exchange → 큐 → 바인딩) | 기동 실패 |
| 3 | EventWorker | 명부 등록 → 몫 계산 → 소비 시작 → Ready | Redis 불통이면 48큐 전부(§7) |
| 4 | IngestApi | Ready (Redis 연결은 백그라운드) | — |
| 5 | Simulator | `--ingest` 대상에 송신. Ingest가 아직 Not Ready면 503 · 연결 실패 → 재시도 | 버퍼가 차면 틱 정지 |

> Ingest가 워커보다 먼저 떠도 안전하다. 토폴로지를 Ingest도 선언하므로 메시지는 큐에 쌓이고, 워커가 마이그레이션을 끝낸 뒤 소비한다.

---

## 13. 작업 분할

`[핵심]` = 중간 관리자(Opus). `[단순 S1~S4]` = 하위 에이전트(Sonnet), 최대 4명 병렬. **묶음끼리 파일이 겹치지 않는다.**
1차: S1 · S2 · S4 병렬(계약 · DB · 서버 구성 · 테스트 장치) → 2차: S3(호스트 조립)는 S1 완료 후. `[핵심]`은 S1 완료 후 시작.

### [단순 S1] 계약 — `src/Shared/Messaging/**`, `src/Shared/Pipeline/**`, `src/Shared/Domain/StatusTransitions.cs`, `tests/Shared.Tests/Messaging/**`

- [ ] `EventBatch` · `EventEnvelope` · 페이로드 record 9종 · `EventType` · `EventCategory` · `FleetJsonContext`
- [ ] `RoutingKeys` · `EventCategories` · `TopologySpec`(08 §8 표, 접두사 지원)
- [ ] 인터페이스 6종 시그니처(§2) · `IngestResult` · `PublishOutcome`
- [ ] `StatusTransitions` (03 표 전체)
- [ ] 테스트 M-01~M-04

### [단순 S2] DB · 서버 구성 — `db/migrations/**`, `src/Infrastructure.Postgres/Migrations/**`, `deploy/compose/infra/**`

- [ ] `001_schema.sql`(01 테이블) · `002_logs_and_state.sql`(§8 추가 테이블) · `003_partition_fn.sql`(함수 · DEFAULT)
- [ ] `Migrator`(advisory lock · 이력 · 체크섬) · `PartitionMaintainer`(기동 · 매시)
- [ ] 통합 테스트 서버용 `deploy/compose/infra/docker-compose.yml` — `rabbitmq:3.13-management`(`enabled_plugins`: `rabbitmq_consistent_hash_exchange`, `rabbitmq_prometheus`, `rabbitmq_management`; `rabbitmq.conf`: 08 §8 브로커 설정 중 단일 노드 해당분) · `redis:7.2`(AOF everysec) · `postgres:16`. 비밀번호는 `.env`(커밋 금지)에서, `.env.example`은 자리표시자만
- [ ] 테스트 D-01~D-03 (`tests/Infrastructure.Tests/Postgres/MigrationTests.cs`, `PartitionTests.cs`)

### [단순 S3] 호스트 조립 — `src/IngestApi/Program.cs` · `Endpoints/**` · `Health/**`, `src/EventWorker/Program.cs` · `Health/**`, `src/PipelineHost/**`, `src/Simulator/SimulationOptions.cs` · `Program.cs`, csproj · `RobotFleet.sln`

- [ ] 프로젝트 생성: `IngestApi` · `EventWorker` · `PipelineHost` · `Infrastructure.RabbitMq` · `Infrastructure.Redis` · `Infrastructure.Postgres` · `Infrastructure.InMemory` · `tools/Reconcile` 와 테스트 프로젝트 3개, 참조 관계, 패키지(`RabbitMQ.Client` 7.1 · `StackExchange.Redis` · `Npgsql` 8 · `prometheus-net.AspNetCore`)
- [ ] `AddFleetIngest` · `AddFleetWorker` · `AddFleetSimulatorEvents` 조립(설정 → 구현 선택, §9 표)
- [ ] 엔드포인트 배선, `/metrics` · `/health/*`(§11 readiness 조건), 워커 관리 포트
- [ ] Simulator 명령행 옵션 추가(§9) · 사용법 문자열 · `SimulationOptionsTests` 추가 행

### [단순 S4] 테스트 장치 · 단순 fake · 도구 껍데기 — `tests/TestSupport/**`, `src/Infrastructure.InMemory/{InMemoryEventSink,InMemoryIdemStore,InMemoryPartitionRoster,FakeIngestClient}.cs`, `tools/Reconcile/Program.cs` · `ReconcileOptions.cs`

- [ ] `IntegrationFactAttribute` · `TestRun`(실행 id · 접두사 · 스키마) · 1시간 지난 자원 청소기 · `FakeTimeProvider` 연결
- [ ] fake 4종(§12 표의 실패 주입 포함)
- [ ] Reconcile 명령행 · 송신 로그 읽기 · `COPY` · JSON 출력 껍데기 (차집합 SQL은 [핵심])

### [핵심] 중간 관리자

- [ ] `src/Simulator/Fleet/{FleetSimulation,TaskGenerator,TaskDispatcher,TaskLifecycle}.cs` + G-01~G-06
- [ ] `src/Simulator/Events/{EventCollector,EventBatchSender,SendLog,HttpIngestClient,InProcessIngestClient}.cs` + E-01~E-04 · S-01~S-07, `RobotStepper` 관찰 지점
- [ ] `src/IngestApi/Core/IngestService.cs`, `src/Infrastructure.Redis/{RedisIdemStore,Scripts/idem_begin.lua,idem_abort.lua}` + I-01~I-16
- [ ] `src/Infrastructure.RabbitMq/{TopologyDeclarer,RabbitEventPublisher,RabbitPartitionSubscriber}.cs` (확인 추적 · 채널 풀)
- [ ] `src/Infrastructure.InMemory/InMemoryBroker.cs`, `InMemoryEventStore.cs` (의미론 충실 fake)
- [ ] `src/EventWorker/Core/{PartitionProcessor,EventHandlers,AssignmentCoordinator}.cs`, `src/Infrastructure.Redis/RedisPartitionRoster.cs` + W-01~W-12 · R-01~R-07
- [ ] `src/Infrastructure.Postgres/PgEventStore.cs` (unnest · RETURNING 집계 SQL) + D-04
- [ ] 계약 기반 클래스 `tests/*/Contracts/*Contract.cs` + C-01~C-04 · T-01~T-03
- [ ] `tools/Reconcile/ReconcileQueries.cs` (차집합 SQL)
- [ ] P-01 · P-02, L3 X-01~X-04 실행 · 보고서

---

## 14. 문서 · README 충돌 지점

| 위치 | 현재 | 수정 |
|---|---|---|
| README 기술 스택 | `RabbitMQ (MassTransit)` | `RabbitMQ (RabbitMQ.Client)` (D1) |
| 01 EventWorker 절 | "실패 시 재시도 → 초과하면 DLQ" | 한 줄 보강: DB 일시 오류는 ack 없이 재시도, 독 메시지 · 전달 횟수 초과만 DLQ → 10 §6 |
| 01 워커 2차 방어 `metrics_minute` 행 | `metrics_applied` Redis Set | `ON CONFLICT … RETURNING`으로 새로 들어간 행만 집계 (D4) → 10 §6 |
| 01 PostgreSQL 테이블 | 영수증 · 상태 테이블 없음 | "추가 테이블은 10 §8" 한 줄 |
| 02 | 충돌 없음 | 수정하지 않는다 |
