# 07. 고가용성

큰 틀만 정한다. 설정 값·Lua 규약·매니페스트·시퀀스는 "상세 설계 위임 항목"에서 중간 관리자가 채운다.

## 목표와 범위

### 견디는 장애

| 장애 | 결과 | 수단 |
|---|---|---|
| 애플리케이션 pod 1개 종료 (FleetController · IngestApi · EventWorker · QueryApi) | 서비스 유지 | replicas ≥ 2 + 재전송 멱등 |
| Redis primary 다운 | 틱 일시 정지 후 재개. 로봇 겹침 0 | Sentinel 장애 조치 + 상태 재주장 |
| RabbitMQ 노드 1개 다운 | task · conflict 유실 0. telemetry 일부 유실 | quorum queue |
| 노드 1개 다운 · drain (node-1~3 어느 것이든) | 위 셋이 동시에 일어나도 유지 | 데이터 계층 3노드 분산 |

### 범위 밖

| 항목 | 이유 | 비고 |
|---|---|---|
| 노드 2개 동시 다운 | 3노드 과반이 깨진다. Sentinel · Raft 모두 멈춤 | 설계상 한계로 명시 |
| Simulator HA | 맵 상태를 가진 단일 프로세스. 재시작 = 새 epoch(01 "Redis 상태 소실 복구") | 재시작 시간만 측정 |
| PostgreSQL HA | 단일 + local PV(Retain). 다운 중 이벤트는 quorum queue에 적체되고 복구 후 소비 | 사용자 확인 필요 (아래) |
| k3s control plane HA | node-1 단일 server. 다운 시 스케줄링만 멈추고 기동 중인 pod는 유지 | 사용자 확인 필요 (아래) |

> **멈춤은 허용, 겹침은 불허.** 어느 장애에서든 "로봇 두 대가 같은 칸" 이 나오는 것보다
> 틱을 몇 초 멈추는 쪽을 택한다. 판단이 불확실하면 `409 state_lost`로 Simulator를 세운다.

---

## 정합성 원칙

| 등급 | 대상 | 규칙 |
|---|---|---|
| **절대 불가** | 같은 칸 이중 승인 | 모든 HA 장치는 이 조건을 먼저 만족해야 한다. 가용성은 그 다음 |
| **절대 불가** | task · conflict 이벤트 유실, 중복 집계 | quorum queue + 01 멱등 2단계 |
| **절대 불가** | 같은 입력에 다른 판정 | 재전송이 다른 인스턴스로 가도 결과가 같아야 한다 |
| 유실 허용 | telemetry · PathReplanned, Redis 캐시 상태, 집계 캐시 | 다음 틱 또는 재주장으로 복구 |
| 유실 허용 | Redis 멱등 키 (`idem:*`) | 워커 2차 방어가 막는다 (01) |

---

## 컴포넌트별 결정

### FleetController — 최대 2대

| 결정 | 내용 | 근거 |
|---|---|---|
| 인스턴스 **2대 고정**, HPA 없음 | 상한도 2 | 한 틱의 배치는 한 인스턴스가 통째로 판정하므로 3대째부터는 판정 처리량이 늘지 않는다. 2대는 가용성과 경로 계산 분담용 |
| 틱마다 로드밸런서가 임의 인스턴스로 | 라운드로빈이면 충분 | Simulator는 응답을 받아야 다음 틱으로 간다 → 같은 맵에 동시 판정이 없다 → 우선순위 정렬과 결정성(01 "배치 내 판정 순서")이 그대로 유지된다 |
| 장애 시 **같은 틱 배치를 다른 인스턴스로 재전송** | 타임아웃 → 재전송 → 소진 시 틱 정지 | 응답만 유실됐을 수 있으므로 재전송은 반드시 **멱등**이어야 한다 |
| 재전송 멱등 | 같은 `(mapId, tick)` 재요청이면 기록된 판정(토큰 포함)을 그대로 반환 | 첫 처리에서 락이 이미 걸렸는데 다시 판정하면 토큰이 달라지고 교착 해소 결정이 갈릴 수 있다 |
| 판정과 경로 계산 분리 | 같은 배포 단위, 다른 엔드포인트. 판정은 틱당 1건 한 곳, A*는 무상태라 어느 인스턴스든 | 판정은 분산할 수 없지만 계산 부하는 나눌 수 있다. `IMoveArbiter` / `IPathPlanner` 경계가 이미 그 선이다 |

> 현재 M2의 `InMemoryMoveArbiter`는 단일 프로세스 전용이다. 2대 운용은 M4에서 Redis 구현으로
> 교체한 뒤에만 가능하다. 그 전에는 replicas 1로 둔다.

### Redis — primary 1 + replica 2 + Sentinel 3

| 결정 | 내용 | 근거 |
|---|---|---|
| Sentinel 3, quorum 2 | 과반 합의로 replica 승격 | 2노드면 split-brain, 3노드면 1개 손실까지 합의 가능 |
| 장애 조치 = 상태 소실로 취급 | 감지하면 `409 state_lost` → Simulator 재주장 → 재개 (01 절차 재사용) | 비동기 복제라 승격된 replica에 직전 틱의 락이 없을 수 있다. **복제본을 믿지 않고 Simulator가 다시 주장**하는 것이 이중 승인을 막는 근본 장치 |
| `min-replicas-to-write 1` | replica가 하나도 안 붙은 primary는 쓰기 거부 | 네트워크 분리로 고립된 옛 primary가 FleetController의 락을 계속 받아 주는 것을 막는다. 감지 전 창의 split-brain 방어 |
| 락 Lua 후 `WAIT 1` | 최소 replica 1대 반영 확인 | 감지 창 동안 새 primary가 직전 틱까지의 락을 갖게 한다. 재주장이 1차, WAIT는 2차 안전망 |
| 영속화는 정합성에 기여하지 않는다 | AOF 여부는 재시작 시간 기준으로만 결정 | 복구의 진실은 Simulator다 |

> **감지가 핵심이다.** 장애 조치가 일어났는데 FleetController가 모르면 다음 틱을 빈 락 위에서
> 판정한다. 감지는 **판정 경로 안에서 배치마다** 이뤄져야 하며, Sentinel 알림 같은 비동기 신호에만
> 의존해서는 안 된다.

### RabbitMQ — 3노드 클러스터, quorum queue

사용자 표현 "active 1 + standby 2, 죽으면 선출"은 RabbitMQ에서 **quorum queue(Raft)** 로 구현된다.
큐마다 leader 1 + follower 2, leader 노드가 죽으면 나머지가 재선출한다.

| 큐 | 타입 | 복제 | 근거 |
|---|---|---|---|
| `fleet.task.p00~p15` | quorum | 3 | 실적 데이터. 유실 불가 |
| `fleet.conflict.p00~p15` | quorum | 3 | 분석 자료. 유실 불가 |
| `fleet.telemetry.p00~p15` | classic, 비복제 | 1 | 유실 허용. 노드 다운 시 그 노드의 파티션은 복귀까지 비어 있음 |

| 결정 | 근거 |
|---|---|
| **M3부터 quorum 타입으로 선언** (멤버 1), M5에서 멤버 3으로 확장 | 큐 타입은 선언 후 바꿀 수 없다. classic으로 만들었다가 지우고 다시 만드는 것보다 `grow`가 낫다 |
| leader를 세 노드에 분산 | 한 노드에 leader가 몰리면 그 노드 장애에 모든 큐가 동시에 재선출 |
| **telemetry 발행 실패는 배치 실패가 아니다** | 노드 다운으로 telemetry 파티션이 수 분간 비면 01의 "하나라도 실패 → 503" 규칙이 task 이벤트까지 막는다. telemetry는 버리고 메트릭만 올린다. task · conflict 실패만 503 |

### 데이터 계층 배치 — 3노드 분산

기존 "데이터 계층 node-1 고정"을 폐기한다. 3복제는 세 노드에 흩어져야 의미가 있다.

| 컴포넌트 | 배치 | 근거 |
|---|---|---|
| Redis ×3, Sentinel ×3, RabbitMQ ×3 | 노드당 1개, **required anti-affinity** | 한 노드가 죽어도 과반이 남는다 |
| PostgreSQL ×1 | node-1 고정 + local PV(Retain) | 범위 밖. 기존 설계 유지 |
| FleetController ×2 | 서로 다른 노드, preferred anti-affinity | 노드 장애에 둘 다 죽지 않게 |
| 나머지 애플리케이션 | 세 노드 모두 허용 | "node-2 / node-3만" 제한 폐기 |

노드 역할은 "control plane · PostgreSQL · 애플리케이션 · 모니터링"이 어디에 붙느냐만 다르고,
Redis · RabbitMQ 멤버는 세 노드 전부에 있다. 서버 권장 사양은 **16GB** 로 올린다.

---

## 트레이드오프

| 선택 | 얻는 것 | 잃는 것 |
|---|---|---|
| FleetController 2대 상한 | 결정성 유지, 설계 단순 | 판정 처리량은 1대와 같다. 한계는 로봇 수 실험(05 실험 6)에서 드러난다 |
| 장애 조치 = 재주장 | 이중 승인 0 | 장애 조치마다 틱이 수 초 멈춘다 |
| `WAIT 1` 매 배치 | 감지 창의 락 보존 | 배치당 왕복 1회 추가. 판정 p99 10ms 예산 안에서 흡수해야 한다 |
| `min-replicas-to-write 1` | 고립 primary 쓰기 차단 | replica 2대가 동시에 빠지면 정상 primary도 쓰기 거부 → 틱 정지 |
| quorum queue 32개 | task · conflict 유실 0 | classic보다 메모리 · 디스크 · 지연이 크다. telemetry를 classic으로 남기는 이유 |
| 데이터 계층 3노드 분산 | 노드 장애 내성 | 모든 노드가 데이터 계층을 짊어진다. node-2 drain 시연도 데이터 계층 장애 조치를 동반한다 |

---

## 로드맵 매핑

| 마일스톤 | 이 문서에서 구현되는 것 |
|---|---|
| M3 | task · conflict 큐를 quorum 타입으로 선언(멤버 1). telemetry 발행 실패 격리 |
| M4 | Redis 기반 Arbiter, 재전송 멱등 Lua, `WAIT 1`, 장애 조치 감지 → `state_lost`, Sentinel 클라이언트 연결, 판정 · 경로 계산 엔드포인트 분리, FleetController ×2 로컬 검증 |
| M5 | Redis · Sentinel · RabbitMQ StatefulSet ×3, anti-affinity, 노드별 local PV, quorum 멤버 3으로 확장, 리소스 표 반영 |
| M8 | 아래 장애 주입 시나리오로 검증. 각 시나리오의 틱 정지 시간 · 유실 수 · 겹침 수 기록 |

---

## 상세 설계 위임 항목

중간 관리자가 채운다. 각 항목의 제약은 총괄이 정한 것이며 바꾸려면 리뷰에서 근거를 든다.

| # | 항목 | 제약 · 원칙 |
|---|---|---|
| 1 | Sentinel 설정 값 (`down-after-milliseconds`, `failover-timeout`, `parallel-syncs`) | 장애 감지부터 틱 재개까지 **30초 이내**. 오탐으로 멀쩡한 primary를 강등시키는 쪽이 더 나쁘다 |
| 2 | `WAIT 1` 타임아웃과 실패 시 동작 | 타임아웃 포함 판정 p99 10ms 안. 타임아웃이어도 그 틱의 승인은 유효하되 degraded 메트릭을 올린다. 연속 실패 시 `state_lost`로 보낼지는 제안할 것 |
| 3 | 장애 조치 감지 방법 | 판정 경로 안에서 배치마다 확인. 감지 시 01 복구 절차(`409` → 재주장) 재사용, 새 epoch는 Simulator가 발급. Sentinel 알림은 보조 |
| 4 | 재전송 멱등 Lua 규약 | 키 `(mapId, tick)`. **락 획득과 판정 기록이 하나의 Lua** (원자). 재요청이면 기록을 그대로 반환. 같은 tick에 다른 요청 본문이 오면 거부. 기록 TTL은 수 틱 |
| 5 | Simulator 재전송 정책 | 타임아웃 · 재시도 횟수 · 소진 시 틱 정지. 재시도 중 `tick`과 `batchId`는 불변 |
| 6 | 판정 · 경로 계산 엔드포인트 분리 | `IMoveArbiter` / `IPathPlanner` 인터페이스 유지. 경로 계산 입력은 맵 · 출발 · 목적 · 혼잡 가중뿐(무상태). 경로 캐시는 인스턴스 로컬 허용 |
| 7 | Redis 클라이언트 연결 | Sentinel 경유 primary 탐색. 장애 조치 후 재연결 시간이 1의 30초 안에 포함된다 |
| 8 | quorum queue 설정 | 32개 quorum + 16개 classic. leader 분산, publisher confirm 유지, SAC 호환 확인, `x-delivery-limit` + DLQ, 메모리 상한 |
| 9 | 01 멱등 2단계 수정 | telemetry 발행 실패는 drop + 메트릭. task · conflict만 503. `idem` 키 상태 전이에 반영 |
| 10 | StatefulSet · anti-affinity · local PV | Redis 3, Sentinel 3, RabbitMQ 3 각각 노드당 1 (required). 각 노드에 PV 경로. PostgreSQL은 node-1 고정 유지. FleetController 2는 preferred |
| 11 | 리소스 표 (02 갱신) | 16GB 서버 기준 request 합 8Gi 이하, limit = request × 2 유지 |
| 12 | M8 장애 주입 시나리오 추가 (02 갱신) | Redis primary kill(겹침 0 · 틱 정지 시간), Sentinel 1 kill(무영향), RabbitMQ leader 노드 kill(task 유실 0 · telemetry 유실 수), FleetController 1대 kill 배치 처리 중(재전송 결과 동일), 데이터 계층이 있는 노드 drain. 시나리오마다 확인 사항과 측정 지표 |
| 13 | 배포 구성도(`docs/images/deployment.svg`) 갱신 | "node-1 고정" · "node-2/3만 애플리케이션" 표기 제거 |

---

## 열린 쟁점

| 구분 | 쟁점 | 총괄 입장 |
|---|---|---|
| 총괄 결정 | FleetController 상한 2 | 3대 이상 금지. 판정 처리량이 부족하면 경로 계산 분리로 해결하고, 그래도 부족하면 04 "부분 분산" 실험으로 넘긴다 |
| 총괄 결정 | telemetry 발행 실패는 배치 실패 아님 | 01 수정은 위임 항목 9 |
| 총괄 결정 | M3부터 quorum 타입 선언 | 01 "M5 이후 전환" 문구 대체 |
| 사용자 확인 필요 | PostgreSQL 단일 유지 | 총괄 제안: 범위 밖. 큐가 버퍼 역할을 하므로 복구 후 적재가 따라온다 |
| 사용자 확인 필요 | k3s control plane 단일(node-1) | 총괄 제안: 범위 밖. 3 server + embedded etcd는 M8 이후 검토 |
| 사용자 확인 필요 | 장애 조치 중 틱 정지 허용 상한 | 총괄 제안 30초. 이 값이 Sentinel 설정의 상한을 정한다 |
