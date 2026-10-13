# 07. 고가용성

큰 틀만 정한다. 설정 값·Lua 규약·매니페스트·시퀀스는 08이 채운다. 08이 07의 제약을 바꾼 곳(C1~C7)은 리뷰를 거쳐 수용했고, 이 문서는 그 결과에 맞춰져 있다.

## 목표와 범위

### 견디는 장애

| 장애 | 결과 | 수단 |
|---|---|---|
| 애플리케이션 pod 1개 종료 (FleetController · IngestApi · EventWorker · QueryApi) | 서비스 유지 | replicas ≥ 2 + 재전송 멱등 |
| Redis primary 다운 | 틱 일시 정지 후 재개. 로봇 겹침 0 | Sentinel 장애 조치 + 상태 재주장 |
| RabbitMQ 노드 1개 다운 | task · conflict 유실 0. telemetry 일부 유실 | quorum queue |
| 노드 1개 다운 · drain (node-1~3 어느 것이든) | 위 셋이 동시에 일어나도 유지. node-1이면 스케줄링 · 재배치도 멈추되 기동 중인 pod는 유지 | 데이터 계층 3노드 분산 |

### 범위 밖

| 항목 | 이유 | 비고 |
|---|---|---|
| 노드 2개 동시 다운 | 3노드 과반이 깨진다. Sentinel · Raft 모두 멈춤 | 설계상 한계로 명시 |
| Simulator HA | 맵 상태를 가진 단일 프로세스. 재시작 = 새 epoch(01 "Redis 상태 소실 복구") | 재시작 시간만 측정 |
| PostgreSQL HA | 단일 + local PV(Retain). 다운 중 이벤트는 quorum queue에 적체되고 복구 후 소비 | 사용자 확정 |
| k3s control plane HA | node-1이 k3s server 겸임. 다운 시 스케줄링만 멈추고 기동 중인 pod는 유지 | 사용자 확정 |

> **멈춤은 허용, 겹침은 불허.** 어느 장애에서든 "로봇 두 대가 같은 칸" 이 나오는 것보다
> 틱을 몇 초 멈추는 쪽을 택한다. 판단이 불확실하면 `409 state_lost`로 Simulator를 세운다.

---

## 정합성 원칙

| 등급 | 대상 | 규칙 |
|---|---|---|
| **절대 불가** | 같은 칸 이중 승인 | 모든 HA 장치는 이 조건을 먼저 만족해야 한다. 가용성은 그 다음 |
| **절대 불가** | task · conflict 이벤트 유실, 중복 집계 | quorum queue + 01 멱등 2단계 |
| **절대 불가** | 관측된 판정이 바뀌는 것 | Simulator가 적용한 판정은 재전송·장애 조치 뒤에도 그대로다. 적용되지 않은 판정은 사라져도 된다 |
| **절대 불가** | 판정 상태(맵 해시)에 커밋·재주장 Lua 밖의 쓰기 | 해시 체인 불변식의 전제. 백그라운드 탐지기든 운영 도구든 체인을 거친다 |
| 유실 허용 | telemetry · PathReplanned, Redis 캐시 상태, 집계 캐시 | 다음 틱 또는 재주장으로 복구 |
| 유실 허용 | Redis 멱등 키 (`idem:*`) | 워커 2차 방어가 막는다 (01) |

---

## 컴포넌트별 결정

### FleetController — 최대 2대

| 결정 | 내용 | 근거 |
|---|---|---|
| 인스턴스 **2대 고정**, HPA 없음 | 상한도 2 | 한 틱의 배치는 한 인스턴스가 통째로 판정하므로 3대째부터는 판정 처리량이 늘지 않는다. 2대는 가용성과 경로 계산 분담용 |
| Simulator가 인스턴스를 고른다 (headless Service) | 성공한 인스턴스에 고정, 실패·409 때만 교대 | Simulator는 응답을 받아야 다음 틱으로 간다 → 같은 맵에 동시 판정이 없다 → 우선순위 정렬과 결정성(01 "배치 내 판정 순서")이 유지된다. ClusterIP는 keep-alive로 고착되고 죽은 pod로 재전송될 수 있어 쓰지 않는다 (08 C6) |
| 장애 시 **같은 틱 배치를 다른 인스턴스로 재전송** | 타임아웃 → 재전송 → 소진 시 틱 정지 | 응답만 유실됐을 수 있으므로 재전송은 반드시 **멱등**이어야 한다 |
| 재전송 멱등 | 같은 `(mapId, tick)` 재요청이면 맵 해시에 기록된 마지막 판정을 그대로 반환. 락 획득과 판정 기록은 하나의 커밋 Lua | 첫 처리에서 락이 이미 걸렸는데 다시 판정하면 교착 해소 결정이 갈릴 수 있다. 기록은 최신 1틱이면 충분하다 — Simulator는 이전 틱을 다시 보내지 않는다 (08 §4) |
| 판정과 경로 계산 분리 | 같은 배포 단위, 다른 엔드포인트. 판정은 틱당 1건 한 곳, A*는 무상태라 어느 인스턴스든 | 판정은 분산할 수 없지만 계산 부하는 나눌 수 있다. `IMoveArbiter` / `IPathPlanner` 경계가 이미 그 선이다 |

> 현재 M2의 `InMemoryMoveArbiter`는 단일 프로세스 전용이다. 2대 운용은 M4에서 Redis 구현으로
> 교체한 뒤에만 가능하다. 그 전에는 replicas 1로 둔다.

### Redis — primary 1 + replica 2 + Sentinel 3

| 결정 | 내용 | 근거 |
|---|---|---|
| Sentinel 3, quorum 2 | 과반 합의로 replica 승격 | 2노드면 split-brain, 3노드면 1개 손실까지 합의 가능 |
| **해시 체인 워터마크**로 저장소 상태를 검증 | 요청마다 Simulator가 마지막으로 적용한 판정의 `(tick, digest)`를 싣고, 저장소의 체인 꼬리와 같을 때만 커밋. 다르면 `409 state_lost` → Simulator 재주장 → 재개 | 비동기 복제라 승격된 replica에 직전 틱이 없을 수 있다. 꼬리가 같으면 뿌리(재주장)부터 이력 전체가 같으므로 칸 점유 = 실제 위치. 손실·분기를 정확히 가려내 불필요한 재주장이 없다 (08 "핵심 장치") |
| 감지는 판정 경로 안에서, 배치마다 | 읽기·커밋 Lua의 체인 검사가 유일한 안전 장치. Sentinel 알림은 재연결을 앞당기는 보조 | 장애 조치를 모른 채 다음 틱을 판정하는 창을 없앤다 |
| `min-replicas-to-write 1` | replica가 하나도 안 붙은 primary는 쓰기 거부 | 고립된 옛 primary가 승격 전에 스스로 쓰기를 멈춘다. 그래도 남는 비대칭 분리는 체인이 막는다 — 안전은 보장, 진행은 미보장 |
| 커밋 Lua 뒤 `WAIT 1` | 최소 replica 1대 반영 확인 | **안전성과 무관.** 승격된 replica가 마지막 틱을 갖고 있게 해 재주장 빈도를 줄인다. 실패해도 승인은 유효, 다음 틱의 체인 검사가 답을 낸다 (08 C2) |
| 칸별 락 키·만료 폐지, 맵당 단일 해시 | 칸 점유·대기 그래프·교착 해소·워터마크를 한 키에, 해제는 명시적으로만 | 칸별 만료는 정지 로봇의 칸을 20틱 뒤 비워 보이게 하고, 틱 정지 중 부분 만료로 구멍을 낸다. 단일 키는 통째로만 사라진다 (08 C5) |
| 영속화는 정합성에 기여하지 않는다 | AOF everysec은 재시작 시간용 | 복구의 진실은 Simulator다 |

> **멈춤은 허용, 겹침은 불허**가 여기서 구체화된다. 저장소가 Simulator가 적용한 마지막 판정을
> 갖고 있지 않으면 판정 자체가 불가능하고, 재주장만이 길을 연다.

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

### 노드 구성 — 노드 3

| 노드 | k3s | 역할 |
|---|---|---|
| node-1 | server (taint 없음) | 애플리케이션, PostgreSQL, Redis · Sentinel · RabbitMQ 멤버 |
| node-2 | agent | 애플리케이션, Redis · Sentinel · RabbitMQ 멤버 |
| node-3 | agent | 애플리케이션, 모니터링, Redis · Sentinel · RabbitMQ 멤버 |

control plane을 따로 두지 않는 이유 — 데이터 계층 과반이 어차피 데이터 멤버가 있는 노드 3대로 닫힌다.
전용 control plane은 메모리를 쓰면서 워크로드를 받지 않아 순수 낭비다. node-1이 server를 겸하되,
node-1 장애 시 API 서버가 함께 내려간다는 제약을 받아들인다 — 기동 중인 pod는 유지되고
스케줄링 · 재배치만 멈춘다. drain 시연은 node-2 또는 node-3을 대상으로 한다.

실행 환경은 집 PC의 VMware VM 3대(`192.168.10.11~13`, 고정 IP)다. M7 부하 실험만 AWS에서 하고,
어느 쪽이든 매니지드 데이터 서비스는 쓰지 않는다 — Sentinel 장애 조치 · quorum 재선출 · 해시 체인이 이 프로젝트의 본체다. 상세는 02.

### 데이터 계층 배치 — 3노드 분산

기존 "데이터 계층 node-1 고정"을 폐기한다. 3복제는 세 노드에 흩어져야 의미가 있다.

| 컴포넌트 | 배치 | 근거 |
|---|---|---|
| Redis ×3, Sentinel ×3, RabbitMQ ×3 | 노드당 1개, **required anti-affinity** | 노드 하나가 죽어도 과반이 남는다 |
| PostgreSQL ×1 | node-1 고정 + local PV(Retain) | 범위 밖. 단일 유지 |
| FleetController ×2 | 서로 다른 노드, preferred anti-affinity | 노드 장애에 둘 다 죽지 않게 |
| 나머지 애플리케이션 · 모니터링 | 세 노드 모두 허용 | "node-2 / node-3만" 제한 폐기. 모니터링은 상시 |

Redis · RabbitMQ 멤버는 세 노드 전부에 있고, 노드별 차이는 k3s server · PostgreSQL · 모니터링이 어디에 붙느냐뿐이다.
노드명은 `--node-name node-1~3`으로 고정한다 — local PV의 `nodeAffinity`가 노드명에 묶이므로 VM을 다시 만들어도 PV가 따라와야 한다.

| 노드 | 사양 | 근거 |
|---|---|---|
| node-1~3 | 각 **2 vCPU · 8GiB** | 시스템 예약 0.6Gi를 뺀 7.4Gi에 request 최대 2.28Gi(합 6.16Gi, 모니터링 포함). 1대가 빠져도 남은 2대에 앱 pod 전부 재배치, 노드당 2.3~2.7Gi. 제약은 메모리가 아니라 **CPU**(호스트 6코어) — EventWorker HPA 상한 3의 이유 (08 §11) |

---

## 트레이드오프

| 선택 | 얻는 것 | 잃는 것 |
|---|---|---|
| FleetController 2대 상한 | 결정성 유지, 설계 단순 | 판정 처리량은 1대와 같다. 한계는 로봇 수 실험(05 실험 6)에서 드러난다 |
| 체인 검증, 불일치 시 재주장 | 이중 승인 0, 손실 없으면 재주장 없음 | 비대칭 분리에서는 진행이 멈출 수 있다. 요청마다 `prev`를 싣고 Redis 왕복이 배치당 2회(읽기·커밋) |
| `WAIT 1` 매 배치 | 재주장 빈도 감소 | 배치당 왕복 1회 추가. 판정 p99 10ms는 클라이언트 가드(9ms)로 지킨다 |
| `min-replicas-to-write 1` | 고립 primary 쓰기 차단 | replica 2대가 동시에 빠지면 정상 primary도 쓰기 거부 → 틱 정지 |
| quorum queue 32개 | task · conflict 유실 0 | classic보다 메모리 · 디스크 · 지연이 크다. telemetry를 classic으로 남기는 이유 |
| 데이터 계층 3노드 분산 | 노드 장애 내성 | 모든 노드가 데이터 계층을 짊어진다. drain 시연은 데이터 계층 장애 조치를 동반한다 |
| 노드 3대, node-1이 k3s server 겸임 (채택) | 서버 3대, 노드 타입 1종. 과반 구성은 전용 control plane이 있을 때와 같다 | node-1 장애 시 API 서버가 함께 내려간다 — 기동 중 pod는 유지, 스케줄링 · 재배치 정지. M8 시나리오로 측정 |

---

## 로드맵 매핑

| 마일스톤 | 이 문서에서 구현되는 것 |
|---|---|
| M3 | task · conflict 큐를 quorum 타입으로 선언(멤버 1). telemetry 발행 실패 격리 |
| M4 | Redis 맵 해시 Arbiter, 해시 체인(읽기·커밋·재주장 Lua), `WAIT 1`, Sentinel 클라이언트 연결, 판정 · 경로 계산 엔드포인트 분리, Simulator 재전송·인스턴스 선택, FleetController ×2 로컬 검증 |
| M5 | 노드 3대 k3s, Redis · Sentinel · RabbitMQ StatefulSet ×3, anti-affinity, 노드별 local PV, quorum 멤버 3으로 확장, 리소스 표 반영 |
| M8 | 아래 장애 주입 시나리오로 검증. 각 시나리오의 틱 정지 시간 · 유실 수 · 겹침 수 기록 |

---

## 상세 설계 위임 항목

08이 채웠다. 제약은 총괄이 정한 것이고, 08이 바꾼 곳은 리뷰에서 근거를 확인해 수용했다.

| # | 항목 | 제약 · 원칙 | 08 |
|---|---|---|---|
| 1 | Sentinel 설정 값 | 장애 감지부터 틱 재개까지 **30초 이내**. 오탐 강등이 더 나쁘다 | §1 · §3 타임라인 |
| 2 | `WAIT 1` 타임아웃과 실패 시 동작 | 가드 포함 판정 p99 10ms 안. 실패해도 그 틱의 승인은 유효, degraded 메트릭. 연속 실패로 `state_lost`를 내지 않는다 | §2 |
| 3 | 장애 조치 감지 | 판정 경로 안에서 배치마다(해시 체인). 감지 시 01 복구 절차 재사용, 새 epoch는 Simulator가 UUIDv7로 발급 | 핵심 장치 · §3 |
| 4 | 재전송 멱등 Lua 규약 | 락 획득 · 판정 기록 · 교착 해소가 하나의 커밋 Lua. 재요청이면 기록 반환, 같은 tick에 다른 본문이면 거부. 재주장은 옛 epoch 거부 | §4 |
| 5 | Simulator 재전송 · 인스턴스 선택 | 타임아웃 · 횟수 · 소진 시 틱 정지. 재시도 중 본문 불변. 성공 인스턴스 고정 | §5 |
| 6 | 판정 · 경로 계산 엔드포인트 분리 | `IMoveArbiter` / `IPathPlanner` 유지. 경로 입력은 맵 · 출발 · 목적 · 혼잡 가중뿐. 캐시는 인스턴스 로컬 | §6 |
| 7 | Redis 클라이언트 연결 | Sentinel 경유. 재연결 시간은 1의 30초 안 | §7 |
| 8 | quorum queue 설정 | 32 quorum + 16 classic. leader 분산, publisher confirm, SAC, delivery limit + DLQ, 메모리 상한 | §8 |
| 9 | 01 멱등 2단계 수정 | telemetry 실패는 drop + 메트릭, task · conflict만 503. Redis 오류 시 멱등 1단계 fail-open | §9 |
| 10 | StatefulSet · anti-affinity · local PV | 데이터 멤버 노드당 1 (required), FleetController preferred, PostgreSQL node-1, taint 없음, 노드명 고정 | §10 |
| 11 | 리소스 표 (02) | 노드 8GiB 기준 request 합 6.16Gi, limit = request × 2, 노드 1대 장애 시 재배치 가능. HPA 상한은 CPU로 결정 | §11 |
| 12 | M8 장애 주입 시나리오 (02) | Redis primary · Sentinel · RabbitMQ leader · FleetController 처리 중 · drain(node-2 · 3) · node-1(API 서버) 장애 · 노드 강제 소실. 시나리오마다 확인 사항 · 지표 · 합격 기준 | §12 |
| 13 | 배포 구성도 갱신 | 노드 3대 구성 | §13 |

---

## 결정 로그

과거 행은 지우지 않는다. 번복은 "(갱신)" 행을 아래에 덧붙여 남긴다.

| 구분 | 쟁점 | 총괄 입장 |
|---|---|---|
| 총괄 결정 | FleetController 상한 2 | 3대 이상 금지. 판정 처리량이 부족하면 경로 계산 분리로 해결하고, 그래도 부족하면 04 "부분 분산" 실험으로 넘긴다 |
| 총괄 결정 | telemetry 발행 실패는 배치 실패 아님 | 01 수정은 위임 항목 9 |
| 총괄 결정 | M3부터 quorum 타입 선언 | 01 "M5 이후 전환" 문구 대체 |
| 사용자 확정 | PostgreSQL 단일 유지 | 범위 밖. 큐가 버퍼 역할을 하므로 복구 후 적재가 따라온다 |
| 사용자 확정 | 노드 구성 cp-1 + worker-1~3 | control plane 1대 단일, 워크로드 없음. HA control plane은 범위 밖 |
| 사용자 확정 | 장애 조치 중 틱 정지 허용 상한 30초 | 이 값이 Sentinel 설정의 상한을 정한다 |
| 사용자 확정 | worker 메모리 6GiB × 3, cp-1 2 vCPU · 2GB | 08 §11 리소스 표의 기준 |
| 사용자 확인 필요 | worker CPU 4 vCPU | 08의 가정. request 합 최대 6.15 core가 worker 1대 장애 후 8 vCPU 안에 든다는 계산의 전제 |
| 사용자 확정 (갱신) | 노드 구성 cp-1 + worker-1~3 → **node-1~3 (3대)** | 전용 control plane 제거. 과반 구성 동일, node-1이 k3s server 겸임 (위 "노드 구성", 02) |
| 사용자 확정 (갱신) | worker 6GiB × 3 + cp-1 2GB, CPU 4 vCPU 가정 → **2 vCPU · 8GiB × 3** | 로컬 호스트 64GB · 6코어. 모니터링 상시 유지. CPU가 유일한 제약이라 EventWorker HPA 상한 3. 08 §11 리소스 표가 기준 |
| 사용자 확정 (갱신) | 자체 리눅스 서버 → **집 PC VMware VM 3대** (M7 부하 실험만 AWS) | 매니지드 데이터 서비스는 계속 쓰지 않는다. 02 환경 절 |
