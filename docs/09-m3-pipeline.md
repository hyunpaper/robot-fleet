# 09. M3 — 이벤트 파이프라인 분리 (큰 틀)

완료 기준(README): **이벤트가 DB에 적재되고 유실 0.** 이 문서는 범위·경계·증명 방법·테스트 전략을 정하고, 상세 설계는 10에서 중간 관리자가 채운다.
08 "구현 작업 분할 M3"의 HA 항목(BatchPublisher · IdemStore · EventBatchSender · Topology · 멱등 테스트)은 그대로 M3에 속한다.

---

## 범위

| 포함 | 제외 |
|---|---|
| Simulator 이벤트 생성 · 틱당 배치 송신 (03 봉투 · `batchId` · 순차 송신) | Redis 기반 Arbiter · FleetController HTTP 분리 (M4) |
| IngestApi — 검증 · 멱등(Redis) · 큐 발행 · 202/503 (01 · 08 §9) | QueryApi · 관제 화면 (M6) |
| RabbitMQ 토폴로지 — exchange 3 · 파티션 큐 48 · DLX/DLQ · `fleet.invalid` (01 · 08 §8, quorum 멤버 1) | 3노드 클러스터 · quorum 멤버 확장 (M5) |
| EventWorker — 파티션 소비 · 배치 INSERT · 증분 집계 · 상태 전이 검증 · DLQ | HPA (M5). 단, 명부 기반 파티션 배정 로직은 M3에서 구현·검증 |
| PostgreSQL 스키마 · 일 단위 파티션 · 마이그레이션 (01) | 보존 정책 자동화(DROP PARTITION 스케줄) |
| Redis 멱등 키 · 워커 명부 (단일 노드) | Sentinel (M5) |
| 최소 메트릭(`/metrics`) · readiness/liveness | Prometheus · Grafana 배포 (M6) |
| 유실 0 대조 도구 | 장애 주입 자동화 (M8) |

> **M3의 Redis는 "있으면 쓰고 없으면 fail-open"이다.** 멱등 키는 유실 허용(07)이고 중복은 워커가 막으므로,
> Redis 없이도 파이프라인이 돌아야 한다. 로컬 개발이 가벼워지고, M4에서 Redis가 필수가 되기 전까지 의존을 늦춘다.

### Simulator 이벤트 — M3에서 실제로 흘러야 하는 것

| 이벤트 | M3 | 근거 |
|---|---|---|
| RobotTelemetry, RobotStatusChanged | **필수** | 지금 Simulator가 아는 사실. 고빈도 큐와 상태 전이 검증의 입력 |
| TaskCreated / Assigned / Started / Completed / Failed | **필수** | "유실 불가" 등급의 대표. 이게 없으면 유실 0 증명이 telemetry(유실 허용)만 재는 꼴이 된다. 작업 생성기(03 포아송)가 아직 없으면 M3에 **최소 생성기**를 포함한다 |
| ConflictDetected | 필수 | InMemoryMoveArbiter 거부 응답을 변환 |
| DeadlockDetected, PathReplanned | 발생하면 발행 | M2 잔여 범위. M3가 생성을 보장하지 않는다 |

---

## 서비스 경계

```
Simulator ──IEventSink──► HttpBatchSender ──HTTP──► IngestApi ──IEventPublisher──► RabbitMQ
                                                        │ IIdemStore (Redis · fail-open)
RabbitMQ ──► EventWorker ──IEventStore──► PostgreSQL
                 │ IPartitionRoster (Redis 명부)
```

| 경계 | 원칙 |
|---|---|
| `src/Shared/Messaging` | 봉투 · 이벤트 DTO · 라우팅 키 규칙 · 토폴로지 선언. **직렬화 가능한 것만**(06 원칙). 5개 서비스가 참조 |
| `src/IngestApi` | 01 그대로: 검증 · 멱등 · 발행 · 즉시 응답. DB를 모른다 |
| `src/EventWorker` | 큐만 소비. HTTP 없음. 핸들러는 이벤트 종류별 순수 함수 + 저장소 인터페이스 |
| `src/Simulator/Events` | 틱 안의 발생 순서로 번호 매김(03), 틱당 배치 1건, 순차 송신 · 재전송 · 버퍼(08 §9). **발행이 틱 예산을 침범하지 않는다** — 송신은 틱 루프 밖 백그라운드, 버퍼 가득 차면 틱 정지 |
| 인프라 접점은 전부 인터페이스 뒤 | `IEventSink` · `IEventPublisher` · `IIdemStore` · `IEventStore` · `IPartitionRoster`. 각각 **인메모리 구현을 먼저** 만들고 실제 구현을 붙인다 (테스트 전략의 전제) |

---

## 완료 기준 — 측정 가능하게

| # | 기준 | 측정 | 합격 |
|---|---|---|---|
| 1 | **task · conflict 유실 0** | Simulator가 송신한 `eventId` 집합(송신 로그 파일) − PostgreSQL 적재 집합(`task_history` 전이 · `conflict_log.event_id`) | 차집합 **0**. 정상 실행 + 워커 프로세스 강제 종료 2회 + Ingest 재시작 1회를 섞은 30분 실행에서 |
| 2 | **중복 0** | 유니크 제약 위반 시도 수(메트릭) vs 반영 행 수 | 위반 시도는 있어도 되고, 집계 · 행 반영은 1회 |
| 3 | **순서 유지** | `fleet.invalid` 큐 적체 · 상태 전이 검증 실패 메트릭 | 워커 2대 + 재배정 1회 포함 실행에서 **0건** |
| 4 | 처리량 | 로봇 200대 · 10Hz(telemetry 초당 2000건) | 큐 적체가 수렴하고 발생 → 적재 지연 p99 ≤ 5초 (01 "허용 지연 수 초") |
| 5 | 틱 예산 보호 | `fleet_tick_overrun_total` | 파이프라인을 붙인 상태에서 200대 기준 틱 초과 0 (M2 단독 실행과 같아야 한다) |
| 6 | telemetry 유실은 허용하되 **센다** | `fleet_telemetry_dropped_total` + 대조 | 수치가 보고서에 남는다 |
| 7 | 테스트 | 단위 테스트는 인프라 없이 전부 통과. 통합 테스트는 원격 서버 접속 정보가 있을 때 전부 통과 | 아래 |

> 유실 0은 "안 잃어버린 것 같다"가 아니라 **송신 집합과 적재 집합의 차집합**으로 증명한다.
> 송신 로그는 Simulator가 배치마다 `{batchId, tick, eventId, eventType}`를 파일로 남기는 것으로 충분하다. M8 §12의 대조 방식과 같은 도구를 쓴다.

---

## 테스트 전략 — 로컬 fake + 원격 인프라

개발 PC에 Docker가 없고, 통합 테스트용 RabbitMQ · Redis · PostgreSQL은 **사용자가 별도 서버에 준비해 접속 정보를 준다.**
서버가 준비되기 전에는 설계와 fake 기반 구현까지만 진행한다.

| 층 | 대상 | 인프라 | 실행 조건 |
|---|---|---|---|
| **L1 단위** | 멱등 상태 전이(08 §9 표 행 단위), 발행 규칙(telemetry drop · 503 판정), 송신기(순차 · 재전송 · 버퍼 · 틱 정지), 워커 핸들러(배치 묶기 · ack 순서 · 상태 전이 검증 · 집계 증분), 파티션 배정 · 재배정 순서(01), 라우팅 키 규칙 | 전부 **인메모리 fake** | 항상. `dotnet test` 기본 |
| **L2 통합** | Redis 멱등 Lua · 명부, RabbitMQ 토폴로지(quorum 인자 · SAC · consistent-hash · confirm · DLQ), Npgsql 배치 INSERT · `ON CONFLICT` · 파티션 | **원격 서버**의 실제 인프라 | 접속 정보가 환경 변수에 있을 때만. 없으면 **skip으로 표시**(실패 아님) |
| **L3 E2E** | 완료 기준 1~6 | 원격 서버 인프라 + 로컬 또는 원격의 서비스 프로세스, 이후 M5 k3s | 수동 · 보고서 첨부 |

| 원칙 | 근거 |
|---|---|
| 핵심 로직(멱등 · 순서 · 배치 · 집계)은 **L1에서 전부** 검증한다. L2는 "인프라가 fake와 같은 의미론을 갖는가"만 본다 | 서버가 없어도 설계 결함은 L1에서 드러나야 한다 |
| fake와 실제 구현은 **같은 계약 테스트**를 돌린다 (`IIdemStore` 계약 테스트를 InMemory · Redis 양쪽에) | fake가 실제와 다르게 동작하면 L1이 거짓 안심이 된다 |
| fake에 **실패 주입**이 있다 (confirm 실패 · nack · 타임아웃 · 저장소 예외 · 중복 전달) | 유실 0 · 중복 0 분기는 실패 경로에 있다 |
| 통합 테스트는 `Category=Integration` trait. 접속 정보는 환경 변수(`FLEET_TEST_RABBITMQ` · `FLEET_TEST_REDIS` · `FLEET_TEST_PG` 같은 연결 문자열)로 주입, 비어 있으면 skip | "서버 없어서 실패"가 없다. 로컬과 CI가 같은 규칙 |
| **접속 정보 · 비밀번호는 저장소에 넣지 않는다.** 코드 · 설정 파일 · 문서 · 커밋 메시지 어디에도. 예시 값은 자리표시자만 | 원격 서버가 실제 자산이다 |
| 통합 테스트는 **자기 자원을 스스로 만들고 지운다** — 테스트별 vhost · 키 접두사 · 스키마(또는 DB) 이름에 실행 id를 붙인다 | 공용 서버를 여러 실행이 나눠 쓴다. 남은 자원이 다음 실행을 오염시키면 안 된다 |
| 서버 없이도 **in-process 파이프라인**을 돌릴 수 있게 한다 — Simulator + InMemory Ingest + InMemory 큐 + InMemory 저장소를 한 프로세스에 조립 | 로컬에서 흐름 전체를 눈으로 확인. M1의 "FleetController를 in-process로 호출"과 같은 방식 |

---

## 총괄 결정

| 결정 | 내용 | 근거 |
|---|---|---|
| 최소 작업 생성기 포함 | 03의 포아송 생성 · Station 무작위 출발/목적 · `Idle → Moving → Loading → Moving → Unloading → Idle` 생애주기 | task 이벤트 없이는 M3 완료 기준 1을 증명할 수 없다 |
| 스키마는 SQL 파일 + 기동 시 적용 | `db/migrations/NNN_*.sql`, EventWorker 기동 시 순서대로 적용(적용 이력 테이블). ORM 마이그레이션 금지 | 01 스키마가 이미 SQL이고 파티션 DDL은 ORM이 못 다룬다 |
| 파티션 사전 생성 | EventWorker가 당일 + 익일 파티션을 기동 시와 자정에 만든다 | 파티션 없는 날짜의 INSERT는 실패 = 유실 |
| 메시징 라이브러리 | README 스택(MassTransit) 유지가 기본. 01 토폴로지(consistent-hash · SAC · 파티션 재배정 · 처리 순서 ack · quorum 인자)를 MassTransit로 구현할 수 없거나 우회가 과하면 `RabbitMQ.Client` 직접 사용으로 전환하고 README를 고친다 | 토폴로지가 라이브러리보다 우선이다 |
| 명부 기반 파티션 배정은 M3 | 워커 2대 로컬 실행으로 재배정 순서(새 몫 구독 → 옛 몫 취소)와 순서 유지를 검증 | HPA 시연은 M5지만 정확성은 M3에서 |
| 워커는 파티션 단위 배치 | 파티션별로 N건 또는 T ms 창으로 모아 INSERT, 처리 완료 순서대로 ack | 01 "개별 INSERT 금지" + 순서 유지 |
| 서비스마다 `/metrics` · `/health/live` · `/health/ready` | 08 메트릭 표의 Ingest · 송신기 항목 + 워커 처리 · 적체 · invalid · DLQ 카운터 | 완료 기준 2~6의 출처 |

---

## 상세 설계 위임 항목 (→ 10)

| # | 항목 | 제약 · 원칙 |
|---|---|---|
| 1 | Simulator 현재 상태 점검 · M3 이벤트 생성 범위 확정 | 작업 생성기 · 상태 전이가 없으면 최소 구현. M2 잔여(교착)는 손대지 않는다 |
| 2 | `Shared.Messaging` 계약 — 봉투 · 이벤트 DTO · 라우팅 키 · 직렬화 | 03 스키마 그대로. 봉투에 `schemaVersion`. `System.Text.Json`, 필드 순서 고정(해시 · 로그 대조용) |
| 3 | 토폴로지 선언 주체 · 시점 | 멱등 선언(있으면 통과). Ingest · Worker 둘 다 기동 시 선언. 08 §8 인자 표 그대로 |
| 4 | IngestApi — 멱등 · 발행 · 응답 | 08 §9 상태 전이 표 그대로. fail-open 경로 메트릭 |
| 5 | Simulator 송신기 | 08 §9: 순차 · 같은 `batchId` 재전송 · 버퍼 600 · 가득 차면 틱 정지. 틱 루프 밖 스레드. 송신 로그 파일(완료 기준 1) |
| 6 | EventWorker — 소비 · 배치 · 집계 · 검증 · DLQ | 파티션별 단일 소비자 · 프리페치 50 · 순서대로 ack. 배치 창 값은 측정으로. 상태 전이 표(03) 위반 → `fleet.invalid`. `x-delivery-limit` 초과 → DLQ. 집계는 `metrics_applied`로 멱등 |
| 7 | 명부 기반 파티션 배정 | 01 "워커 수가 변할 때" 그대로. Redis 없으면 단일 워커 · 전 파티션 |
| 8 | PostgreSQL — 스키마 · 마이그레이션 · 파티션 · 배치 INSERT 방식 | 01 테이블 · 유니크 키 그대로. `unnest` 배열 INSERT 권장(파라미터 수 제한 회피). 커넥션 풀 크기 근거 |
| 9 | 실행 구성 — in-process 모드(인프라 없음) · 원격 인프라 모드 · (M5) k3s | 셋 다 같은 조립 코드. 설정으로만 갈라진다. 접속 정보는 환경 변수 |
| 10 | 유실 0 대조 도구 | 송신 로그 형식 + 대조 SQL/스크립트. M8 §12와 공용 |
| 11 | 메트릭 · 헬스 | 08 메트릭 표 + 워커 항목. readiness는 02 규칙(큐 · DB 연결) — FleetController의 예외(08 §5)는 M4 |
| 12 | 테스트 목록 (06 스타일) · fake 설계 · 실패 주입 지점 · 환경 변수 skip 방식 · 자원 격리(실행 id) | L1 · L2 · L3 구분과 계약 테스트 명시 |
| 13 | 작업 분할 — `[핵심]` / `[단순 S1~S4]` | 08 분할표 형식. 묶음끼리 파일이 겹치지 않게 |
| 14 | README · 01 · 02 충돌 지점 최소 수정 | 메시징 라이브러리를 바꾸면 README 스택 표 |

---

## 리뷰 승인 조건

1. 완료 기준 1~7 각각 "어느 코드 · 어느 테스트 · 어느 스크립트"가 증명하는지 표로 대응된다.
2. 멱등(08 §9) · 순서(01 파티션 · SAC · 명부) · 배치 ack 규칙이 L1 테스트 목록에 **행 단위로** 있다. 실패 주입 케이스(confirm 실패 · 워커 kill 재전달 · Redis 없음)가 포함된다.
3. fake 5종이 계약 테스트로 실제 구현과 묶여 있고, 통합 테스트가 접속 정보 없으면 skip(실패 아님)으로 표시되며, 자원 격리와 정리가 있다. 접속 정보가 저장소 어디에도 없다.
4. Simulator 송신이 틱 루프를 막지 않는 구조이고, 버퍼 상한과 틱 정지 조건이 명시된다.
5. 파티션 사전 생성 · 마이그레이션 적용 순서 · 기동 시 토폴로지 선언 — "처음 띄울 때" 경로가 빠짐없이 있다.
6. 메시징 라이브러리 선택에 01 토폴로지 항목별 가능/불가 근거가 있다.
7. 설정 값마다 근거 한 줄. 측정으로 정할 값은 "M3 측정"으로 표시.
8. 작업 분할이 핵심/단순으로 나뉘고 묶음 간 파일 충돌이 없다.
9. 문서 톤: 표 + 짧은 근거 + `>` 강조.

---

## 사용자 확인 필요

| 쟁점 | 총괄 입장 |
|---|---|
| 통합 테스트 서버 — 접속 정보 전달 시점 · 방식 | 서버 준비 전에는 L1 · in-process까지. L2 · L3는 접속 정보를 받은 뒤. 완료 선언은 L3까지. 접속 정보는 환경 변수로만 받고 저장소에 남기지 않는다 |
| 원격 서버의 RabbitMQ 버전 · 플러그인 | consistent-hash exchange 플러그인과 quorum queue가 켜져 있어야 한다(3.13+ 권장). 준비 시 확인 요청 |
| M2 잔여(교착 탐지 · 해소)와 M3 순서 | M3를 먼저 간다. 파이프라인은 교착 이벤트가 없어도 완성되고, M2 잔여는 M3 위에서 DeadlockDetected를 흘려보내며 검증하는 편이 낫다 |
| 최소 작업 생성기를 M3에 포함 | 포함 (위 총괄 결정). M2 범위라고 보면 M2 잔여로 옮길 수 있으나 M3 완료 기준 1이 그에 묶인다 |
