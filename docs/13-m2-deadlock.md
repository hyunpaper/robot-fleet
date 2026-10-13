# 13. M2 상세 설계 — 교착 탐지와 해소

04 "교착 탐지와 해소"의 큰 틀·규칙 아래 M2 잔여(탐지·양보·재계획·이벤트)를 구현 단위로 채운다.
10과 같은 톤(표 + 근거 한 줄 + `>` 강조)으로 값마다 근거를 붙인다. **04 기본값·안전성 불변식은 바꾸지 않는다.**

---

## 1. 범위와 현재 상태

| 구성 | 현재 | M2에 필요한 것 |
|---|---|---|
| `InMemoryMoveArbiter` | 2칸 선점·점유 판정, 거부 시 `BlockedBy`로 대기 관계 산출(실제 판정 로직). `MoveDenyReason.Deadlock`·`MoveDecision.YieldTo`는 **자리만** 있고 미사용 | 대기 그래프 순환 탐지 + 양보 결정을 **판정 안에서** |
| 1칸 통로 정면 대치 | 영구 `Waiting`(`HeadOn_InCorridor_BothStayWaiting` 고정) | 회피 지점 있으면 해소, 없으면 `failed` |
| `DeadlockDetectedPayload` | `Cycle·Cells·Resolution·YieldRobotId·YieldToX/Y·ResolvedMs` | **`SearchedDistance` 없음** — 추가(03은 이미 문서화) |
| `conflict_log.searched_distance` | DDL(001)에 컬럼 있으나 `StoreMapping`·`PgEventStore`가 **안 씀** | 적재 경로에 연결 |
| `PathReplanned` | Simulator가 **발행하지 않음** → `replan_log` 실데이터 없음 | 재계획 트리거에서 발행 |
| 재계획 트리거·쿨다운·상한 | 없음(대기만) | 04 "재계획 트리거"·"폭주 방지" 구현 |

구현 순서는 04 "구현 순서" 6~8단계(교착 탐지 → 양보 → 재계획 정책)다.

---

## 2. 경계 — 탐지·해소는 배치 판정 안에서

> **교착 탐지·해소는 `DecideAsync`(배치 판정) 안에서 한다.** 04 해소 절차 1·4단계가 "배치 판정 6단계 안 → 배치 응답에 deadlock 항목"이고, `MoveDecision`에 이미 `MoveDenyReason.Deadlock`·`YieldTo` 자리가 있다. 별도 호출(이전 초안의 `ResolveDeadlocksAsync`)은 **폐기**한다.

### 왜 판정 안인가 (별도 호출 폐기 근거)

| 이유 | 설명 |
|---|---|
| 08 §4 재전송 멱등 | 배치 응답은 기록되고 재전송 시 **그대로 반환**된다. 별도 호출의 해소 결정은 그 기록에 안 들어가 **재계산되거나 사라진다** |
| M4 비용 | 틱마다 Redis·HTTP 왕복이 하나 더 는다. 판정 커밋 Lua(08 §4 `wg`·`dl`·`yl`) 하나에서 끝나야 한다 |
| 입력이 이미 있다 | `WaitMs`·`TaskPriority`는 이미 `MoveRequest`에 있다. 점유·예약은 중재기 내부 상태다 — 외부 스냅숏이 필요 없다 |

### 구조

```csharp
namespace RobotFleet.FleetController.Arbitration;

public enum DeadlockPhase : byte { None = 0, Detected = 1, Resolved = 2, Failed = 3 }

/// 판정 응답에 실리는 교착 정보. 같은 틱의 MoveDecision 하나(탐지 시작 로봇 또는 양보 로봇)에 붙는다
public readonly record struct DeadlockInfo(
    IReadOnlyList<string> Cycle, IReadOnlyList<Point> Cells, string Resolution,   // "yield" | "failed"
    string? YieldRobotId, Point? YieldTo, int SearchedDistance,
    long DetectTick, int ResolvedMs, DeadlockPhase Phase, bool IsNew);

public readonly record struct MoveDecision(
    string RobotId, bool Granted, MoveDenyReason Reason = MoveDenyReason.None,
    string? BlockedBy = null, Point? YieldTo = null, DeadlockInfo? Deadlock = null);   // Deadlock 필드 추가
```

| 결정 | 근거 |
|---|---|
| `DecideAsync` **시그니처 유지**, 응답의 `MoveDecision`에 선택 `Deadlock` 필드 | 기존 호출·테스트 회귀 없음. 배치 응답 하나에 해소가 실려 재전송 시 그대로 반환된다 |
| `InMemoryMoveArbiter`가 판정 끝(점유 확정 후)에 **내부 순수 `DeadlockResolver`** 호출 | 판정과 같은 틱·같은 상태. M4는 같은 자리를 커밋 Lua로 옮긴다 |
| 중재기 = **상태 단일 출처** | 해소 락·양보 표시·완료 감지 모두 중재기 안(InMemory → M4 Redis `dl`·`yl`). Simulator는 해제 API를 부르지 않는다(§5·§8) |
| `AlwaysGrantMoveArbiter`(M1)는 `Deadlock = null` | 교착은 점유 판정이 있을 때만 난다. M1 회귀 없음 |

### run 종료 드레인

진행 중 교착(탐지됐으나 미완료)을 종료 시 마무리한다.

```csharp
public interface IMoveArbiter   // 기존 메서드 유지 + 종료 마무리 하나
{
    /// run 종료: 아직 해소되지 않은 교착을 failed(resolvedMs=경과)로 확정해 돌려준다. 중재기가 스스로 끝내는 것(Simulator의 해제 아님)
    IReadOnlyList<DeadlockInfo> DrainPendingDeadlocks(string mapId, long tick);
}
```

| 호출 규약 | 결정 | 근거 |
|---|---|---|
| 호출 지점 | `FleetSimulation` **종료 경로 한 곳**, `SimulationEnded` 발행보다 **먼저**. run 도중 호출 금지 | 드레인은 종료 마무리다. 도중에 부르면 살아 있는 교착을 failed로 잘못 닫는다 |
| 멱등 | 재호출 시 **같은 목록** 반환(빈 목록 금지) | M4 HTTP 재시도 시 유실 방지(08 §4 원칙). 한 번 failed로 확정한 교착은 상태에 남겨 다시 같은 목록을 준다 |
| M4 순서 | run 전환 시 **맵 해시 초기화·epoch 증가(02 run 관리 4단계)보다 앞** | 초기화가 먼저면 드레인 대상이 사라진다 |

---

## 3. 대기 그래프와 순환 탐지

### 그래프 — 함수형, 이번 틱 거부에서만

간선: `robotId → BlockedBy`(점유 owner 또는 swap 상대). **이번 틱 거부 결정에서만** 만든다.

| 조건 (총괄 지시) | 근거 |
|---|---|
| 간선은 **이번 틱 거부 결정에서만**, 지난 틱 잔존 금지 | 대기 관계는 매 틱 판정으로 새로 난다. 묵은 간선은 이미 풀린 교착을 교착으로 본다 |
| 로봇 1대당 거부 **1건** `assert` | 요청 칸 1개 → 막은 로봇 1개. 둘 이상이면 판정 버그 → 즉시 드러낸다 |
| out-degree ≤ 1 → 순환 **서로소** | 04의 "중첩 순환"은 이 모델에서 안 난다. 한 틱 여러 순환은 **독립 ρ 모양**이라 각각 따로 푼다 |
| M4 `wg`도 같은 성질 유지 | Redis 대기 그래프도 이번 틱 거부만 기록, 로봇당 1간선. 같은 순수 탐지가 돈다 |

### 탐지 시작·알고리즘

```
시작: WaitMs >= deadlock_detect_wait_ms 인 거부 로봇만(04). 짧은 대기는 곧 풀리므로 보지 않는다
찾기: 그 로봇에서 BlockedBy를 따라가며 방문 집합에 다시 나오면 순환(04). 함수형이라 O(체인), 보통 2~4
```

| 결정 | 근거 |
|---|---|
| 시작 로봇을 **판정 정렬 순서**(점수 내림차순, 동점 robotId 오름차순)로 | 같은 입력 → 같은 순환·양보. 결정성(§9) |
| 서로소 순환 여러 개를 한 틱에 해소. 이미 양보 표시된 로봇이 낀 순환은 건너뜀 | 독립 순환은 서로 영향 없음. 겹치면 04 양보 표시로 재해소 방지 |
| `Cycle`(이벤트) = 방문 순서, `cycleKey`(락) = robotId 정렬 | 03 이벤트는 발견 순서, 락 키는 시작 로봇 무관 |

```csharp
public static class DeadlockGraph   // 순수, 테스트
{
    public static IReadOnlyList<IReadOnlyList<string>> FindCycles(
        IReadOnlyDictionary<string, string> waitEdges, IReadOnlyDictionary<string, int> waitMs, int thresholdMs);
}
```

---

## 4. 양보 로봇 선정 · 회피 지점

### 양보 로봇 선정 (04 비용식, 기본값 불변)

```
yieldCost = yield_cost.distance_weight × (회피 지점까지 거리)
          + yield_cost.priority_weight × (1 / 작업 우선순위)      // 우선순위 0이면 1로 본다(0분모 방지)
```

순환의 각 로봇에 회피 지점 BFS로 거리를 구해 비용 최소를 고른다. 동점은 robotId 오름차순. **회피 지점을 하나도 못 찾은 로봇은 후보에서 뺀다.** 순환의 **모든** 로봇이 회피 지점이 없으면 `failed`(§5).

### 회피 지점 BFS — 점유·예약 칸만 (M2·M4 통일)

현재 칸에서 4방향 BFS(방향 순서 N·E·S·W). **경로 칸도 미점유·미예약 칸으로만 확장**(시작 칸만 예외), 가장 가까운 유효 칸.

| 항목 | 결정 | 근거 |
|---|---|---|
| 유효 칸 술어 | **통행 가능 + 미점유 + 어느 로봇의 예약 칸(현재·목표)도 아님** | M2·M4 **동일 술어**. M2만 전체 경로로 검사하면 M2 테스트가 증명한 동작이 M4 제품에 없다(M7은 M4 이후). `RemainingPath` 스냅숏 **제거** |
| 경로 칸 | 시작 칸을 뺀 **모든 경유 칸이 미점유**여야 확장(양보 로봇이 실제로 도달 가능) | 목표 칸만 보면 중간이 막혀 못 간다 |
| 상한 | `yield_search_max_distance`(기본 5) 홉 | 04 |
| 범위 확대 | 상한에서 못 찾으면 **+2**까지 한 번 늘려 재탐색 | 04 파생 제약(TTL ≥ 거리×10, 거리 한 번에 +2) |
| 실패 | 확대 후에도 없으면 `failed`, `SearchedDistance` = 마지막 탐색 상한 | 04 "맵 설계 문제 신호", 03 `no_yield_point` |

> 04 "다른 로봇의 계획 경로가 아님"은 **2칸 선점 모델에서 점유·예약 칸으로 판정한다**는 해석 주석만 04에 단다(기본값·불변식 불변). 전체 경로는 Redis에 없다 — 점유·예약이 아는 전부다.

```csharp
public static class YieldSearch   // 순수, 테스트
{
    /// reserved(cell): 점유·예약이면 true. 반환: (회피 지점, 실제 탐색 홉). 못 찾으면 null·마지막 상한
    public static (Point? YieldPoint, int Searched) Find(GridMap map, Point from, int maxDistance, Func<Point, bool> reserved);
}
```

### 양보 이동이 막힐 때 — 양보 중 로봇 우선

| 항목 | 결정 | 근거 |
|---|---|---|
| 양보 중 로봇의 판정 점수 | **모든 로봇 앞으로**(동점 이전에 "양보 중"을 먼저 거르는 선행 버킷) | 04 우선순위 기본값은 그대로 두고, 양보 이동이 비양보 로봇에 막히지 않게 |
| 제3의 양보 로봇·정지 로봇에 막히면 | 그 틱 대기 유지 | 억지로 풀면 또 다른 교착 |
| TTL까지 도달 못 하면 | §5 — TTL 만료 시 failed(resolvedMs=경과) 발행 후 재탐지 | 영원히 매달리지 않게 |

---

## 5. 해소 절차 · 락 · resolvedMs

### 절차 (04 "해소 절차"의 단일 프로세스·판정 안 판)

```
1. 탐지(대기 임계 초과 + 순환) — DecideAsync 판정 끝, 내부 DeadlockResolver
2. 해소 락 cycleKey로 획득(메모리). 이미 있으면 저장된 결정을 응답에 실어 반환(IsNew=false) — 재계산 안 함
3. 양보 로봇 선정 + 양보 표시, 락 값에 기록. 응답 MoveDecision.Deadlock = {Detected, yield, IsNew=true}
4. Simulator: 양보 로봇을 회피 지점으로 재계획(interim 목표), Waiting→Moving(yield), PathReplanned(deadlock). DeadlockDetected는 아직 발행 안 함
5. 중재기가 점유로 양보 로봇의 회피 지점 도착을 감지 → 그 틱 응답 Deadlock = {Resolved, resolvedMs, IsNew=true}. Simulator가 DeadlockDetected(yield) 1회 발행
6. 양보 로봇은 **순환의 다른 로봇이 순환 칸을 벗어난 뒤** 원래 목적지로 재계획(PathReplanned deadlock). 나머지는 path_cleared 또는 재계획
```

> **회피 지점 대기 생존성 상한(§5-6).** 순환의 다른 로봇이 그 칸에서 작업에 진입하는 등으로 **끝내 안 벗어날 수 있다.** 그래서
> 양보 로봇은 Resolved 이후 `deadlock_resolve_ttl_ticks`가 지나도 순환 칸이 안 비면 **그냥 원래 목적지로 재계획**한다(막은 칸을
> 장애물로, §6-a). 이 재계획도 `replan_max_per_task` 상한에 센다 — 영원히 회피 지점에 매달리지 않는다.

### 발행 시점과 누락 경로 (완결성)

| 경우 | DeadlockDetected 발행 | resolvedMs |
|---|---|---|
| `yield` 완료 | Resolved 신호 틱(§5-5) | `(완료틱 − 탐지틱) × tickMs` — 중재기 탐지 틱 기준 |
| `failed`(회피 지점 없음) | 탐지 틱 즉시 | 0 |
| `yield` 미완료 **TTL 만료** | 만료 틱에 **failed**로 | 경과 `(만료틱 − 탐지틱) × tickMs` |
| run 종료 시 진행 중 | `DrainPendingDeadlocks`가 **failed**로 | 경과 |

> **완료 시점 발행만 두면 양보 막힘·TTL 만료·run 종료에서 영원히 미발행**이 되어 conflict_log 누락, 송신 로그에도 없어 Reconcile도 못 잡는다. 그래서 세 누락 경로를 모두 failed로 닫는다.

### 해소 락 — 중재기 상태 단일 출처

| 항목 | 결정 | 근거 |
|---|---|---|
| 키·값 | `cycleKey`(robotId 정렬) → `{탐지틱, resolution, yieldRobotId, yieldTo, searchedDistance}` | 04. 시작 로봇 무관하게 수렴 |
| TTL | `deadlock_resolve_ttl_ticks`(기본 50틱), 락·양보 표시 모두 | 04. 후퇴 5칸×10틱 |
| 완료 감지·해제 | **중재기가 점유로 직접 감지**(양보 로봇이 회피 지점 점유). Simulator 해제 API 금지 | §8 상태 단일 출처. 두 번 실행·발행 방지 |
| IsNew 표시 | 새 결정·완료·실패는 `IsNew=true`, 재전송·재사용은 `false` | Simulator가 같은 해소를 두 번 발행·실행하지 않게(08 §4) |
| failed도 락을 TTL 유지 | 재탐지·이벤트를 **TTL 주기 이하로 묶는다** | 회피 지점 없는 통로에서 매 틱 재탐지·재발행 폭주 방지 |

### failed 이후 동작

| 항목 | 결정 | 근거 |
|---|---|---|
| 어느 로봇 실패 | 순환의 **전원** 작업을 `TaskFailed`(reason `deadlock_unresolved`) | 회피 지점이 하나도 없는 순환은 집합이 풀 수 없다. 한 대만 실패시키면 나머지가 실패 신호 없이 영구 정지 |
| 실패 로봇 상태 | `Waiting → Error`(reason `deadlock_unresolved`, 03 전이표에 이미 있음). `Error → Idle`은 `recovered`(M8)로만 | 03 line 516. Error라 더는 이동 요청 안 함 → 대기 그래프에서 빠져 재탐지 없음 |
| 이벤트 | 순환당 `DeadlockDetected`(failed) **1건**(탐지 시작 로봇에 귀속), `TaskFailed`는 로봇마다 | 03 이벤트는 로봇 귀속, 교착은 순환 단위 |
| 락 | failed도 TTL 유지 → 같은 순환 재탐지는 TTL 뒤에만 | 위 폭주 방지. 전원 Error면 간선이 사라져 재탐지 자체가 없다 |

> M2 단일 프로세스라 락 경쟁은 사실상 없다(04: 한 틱 배치는 한 주체). 락을 두는 이유는 **TTL 기반 재탐지·재발행 억제**와 **M4 Redis 락으로 코드가 그대로 이어지게** 함이다.

---

## 6. 재계획 트리거 · 폭주 방지 — 교착과의 순서

04 "재계획 트리거"·"폭주 방지"를 `TaskLifecycle`에 둔다. **기본값에서 재계획 임계(500) < 교착 탐지(1000)이라, 정면 대치가 교착 탐지 전에 `replan_exceeded`로 실패하는 문제**를 막는다.

| 규칙 (총괄 지시) | 내용 |
|---|---|
| (a) blocked 재계획은 **막은 로봇 칸을 일시 장애물**로 놓고 탐색 | 지금 막힌 칸을 피하는 대안을 찾는다 |
| (b) 대안이 없거나 **같은 경로**면 재계획으로 치지 않음 | `PathReplanned` 미발행, 상한 미계산, **대기 유지** → 교착 탐지(1000ms)로 넘긴다. 정면 대치는 대안이 없어 여기 걸린다 |
| (c) 재계획 뒤 `WaitMs` | 성공(새 경로)하면 0으로 초기화, (b)로 대기 유지면 **누적 유지**(교착 탐지의 대기 임계를 채워야 하므로) | |

| 트리거 | 조건 | reason |
|---|---|---|
| 대기 초과 | 누적 `WaitMs` > `replan_wait_threshold_ms`(500) **이고 대안 있음** | `blocked` |
| 교착 양보 | §5 4·6단계 | `deadlock` |
| 맵 변경 | (M2 범위 밖, 자리만) | `map_changed` |

| 폭주 방지 | 값 | 처리 |
|---|---|---|
| 로봇당 쿨다운 | `replan_cooldown_ms`(100) | 직전 재계획 후 쿨다운 내면 재계획 안 함 |
| 작업당 상한 | `replan_max_per_task`(5) | 초과 시 `TaskFailed`(reason `replan_exceeded`), `Moving→Error(replan_exceeded)` |
| deadlock 사유 재계획 **상한에 포함** | — | 끝없는 흐름에서 같은 로봇이 계속 양보해도 **유한하게 실패**(맵 설계 문제 신호). 핑퐁·기아 방지(§11 DL-09) |

재계획 = `planner.FindPath(현재→목표)`(막은 칸 장애물) → 성공 시 `PlannedPath` 교체. `PathReplanned`: `oldLength`=남은 옛 경로, `newLength`=새 경로, `computeMs`=`PathResult.Elapsed`.

---

## 7. 이벤트 · 파이프라인 반영

### 발행 (EventCollector)

| 이벤트 | 추가 | 배치 순서(rank) |
|---|---|---|
| `DeadlockDetected` | `EventCollector.Deadlock(robotId, payload)`. `SearchedDistance` 포함, `resolution` ∈ {yield, failed} | 로봇 묶음 rank 0 |
| `PathReplanned` | `EventCollector.Replan(robotId, payload)` | rank 0 |

완료·TTL 만료·run 종료 발행 경로(§5)를 모두 EventCollector로 흘린다 → conflict_log·Reconcile 누락 없음.

### 스키마·적재 (searched_distance 연결, [핵심] 보유)

| 파일 | 변경 |
|---|---|
| `src/Shared/Messaging/Payloads.cs` | `DeadlockDetectedPayload`에 `int SearchedDistance`(03 JSON에 이미 있음) |
| `src/Shared/Pipeline/StoreMapping.cs` | `ConflictLogChange`에 `short? SearchedDistance`, `ToConflict`가 교착에서 채움(ConflictDetected는 NULL) |
| `src/Infrastructure.Postgres/PgEventStore.cs` | `conflict_log` INSERT에 `searched_distance`(DDL 001:82에 이미 컬럼) |

### 03 전이·사유 (대부분 이미 있음)

| 전이/사유 | 03 현재 | 필요 |
|---|---|---|
| `Waiting → Error (deadlock_unresolved)` | **있음**(line 516) | 그대로 |
| `Moving → Error (replan_exceeded)` | **있음**(line 515) | 그대로 |
| `Waiting → Moving (yield·replanned)` | **있음**(line 516) | 그대로 |
| `TaskFailed.reason` `deadlock_unresolved` | **있음**(line 369) | 그대로 |
| `TaskFailed.reason` `replan_exceeded` | **없음** | **추가**(§9) |

> 재계획 상한 초과 사유를 03 상태표의 기존 토큰 **`replan_exceeded`**로 통일한다(총괄이 쓴 `replan_exhausted` 대신 — 03 line 515가 이미 `replan_exceeded`라 토큰 중복을 피한다). `deadlock_unresolved`는 이미 TaskFailed·전이표 양쪽에 있어 추가 없음.

---

## 8. 설정 옵션 (교착 키 옵션화)

전면 외부화는 M4 직전(04)이지만, 교착·재계획 키는 **지금 옵션 객체**로 받는다(상수 금지). 기본값 = 04 "## 설정값".

```csharp
namespace RobotFleet.FleetController.Arbitration;

public sealed record DeadlockOptions   // 04 설정값 9~13
{
    public int DeadlockDetectWaitMs { get; init; } = 1000;      // 9
    public int YieldSearchMaxDistance { get; init; } = 5;       // 10
    public double YieldDistanceWeight { get; init; } = 1.0;     // 11
    public double YieldPriorityWeight { get; init; } = 1.0;     // 12
    public int DeadlockResolveTtlTicks { get; init; } = 50;     // 13
    public void Validate();   // TTL >= 거리 × 10 (04 연동 제약), 어기면 ArgumentException
}
```

재계획 키 6~8(`replan_wait_threshold_ms`·`replan_cooldown_ms`·`replan_max_per_task`)은 Simulator라 `FleetOptions`에 더한다(기본 500·100·5). 설정 파일·환경변수 바인딩은 M4 직전 — 지금은 객체 기본값 + 테스트 주입만.

---

## 9. 결정성 규칙 요약

같은 입력(점유·요청·대기시간·옵션) → 같은 출력(배치 재전송·M4 재사용의 전제).

| 지점 | 규칙 |
|---|---|
| 대기 간선 | 이번 틱 거부만, 로봇당 1건 |
| 탐지 시작 순서 | 판정 점수 내림차순, 동점 robotId 오름차순 |
| 양보 로봇 동점 | robotId 오름차순 |
| 회피 지점 BFS | 방향 N·E·S·W, 큐 FIFO → 같은 거리면 방문 순서 고정 |
| 양보 중 우선 | 비양보보다 먼저(선행 버킷), 그 안에서 04 기본 점수 |
| 락 키 | robotId 정렬(발견 순서 무관) |
| 발행 1회 | `IsNew`로 재전송·재사용 중복 발행 차단 |

---

## 10. 04 · 03 대비 변경점 (총괄 리뷰 대상)

| 위치 | 원문 | 13 구현 | 근거 |
|---|---|---|---|
| 해소 호출 | (초안) 별도 `ResolveDeadlocksAsync` | **폐기, `DecideAsync` 판정 안**. `MoveDecision.Deadlock` 필드 | 08 §4 재전송 멱등·M4 왕복 — 판정 응답에 실려야 기록·재사용된다 |
| 회피 지점 조건 | 04 "다른 로봇 계획 경로가 아님" | **점유·예약 칸으로 판정(M2·M4 동일)**, `RemainingPath` 제거 | M2 전용 검사는 M4 제품에 없는 동작을 만든다. 04에 해석 주석만 |
| BFS 확장 | (초안) 목표 칸 조건만 | **경로 칸도 미점유**(시작 칸만 예외) | 중간이 막히면 양보 로봇이 못 간다 |
| DeadlockDetected 발행 | 03 미규정 | yield=완료, failed=탐지, **TTL 만료·run 종료=failed(경과)** | 누락 경로 차단(conflict_log·Reconcile) |
| failed 이후 | (초안) 미정 | 순환 **전원** TaskFailed(deadlock_unresolved)·Waiting→Error, 락 TTL 유지 | 한 대만 실패시키면 나머지 영구 정지 |
| 재계획 vs 교착 | (초안) 미정 | 대안 없으면 재계획 안 침, 대기 유지→교착 탐지. deadlock 재계획도 상한 포함 | 정면 대치가 교착 전에 실패하지 않게 |
| `DeadlockDetectedPayload` | 03 JSON에 `searchedDistance` 있음 | **코드 record에 필드 추가** | 03과 코드 일치 |
| `searched_distance` 적재 | 01 DDL에 컬럼 있음 | 매핑·INSERT 연결 | 비어 있던 경로 |
| `TaskFailed.reason` | 03에 `replan_exceeded` 없음 | **03 TaskFailed 목록에 `replan_exceeded` 추가**(값 불변, 기존 전이표 토큰과 통일) | §7 |

> 03은 `replan_exceeded`를 TaskFailed 사유에 더하는 **한 줄**만 바뀐다(전이·`deadlock_unresolved`는 이미 있음). 04·01 문서·기본값·안전성 불변식은 바꾸지 않는다. 위 구현 판단들이 리뷰 대상이다.

> **`failed` 두 뜻 구분.** TTL 만료로 닫힌 failed는 **`yield_robot_id`가 있다**(양보를 시작했으나 못 끝냄). 회피 지점이 아예
> 없어 닫힌 failed는 **`yield_robot_id`가 NULL**(§5 failed, P1-2). 그래서 M9 `no_yield_point` 판정은
> **"`resolution = failed` AND `yield_robot_id IS NULL`"**이다 — `searchedDistance = 상한`으로만 가르면 확대 범위 끝에서
> 찾아 양보까지 간 경우(TTL 만료 failed)를 오분류한다. 03 `no_yield_point` 입력(04 M9)에 이 조건을 적용.

---

## 11. 테스트 (순수 로직 — 실제 판정이라 인프라 없이 돈다)

| ID | 대상 | 검증 |
|---|---|---|
| DL-01 | `DeadlockGraph.FindCycles` | 2-순환(정면), 3-순환, 추종(순환 아님), 서로소 2순환, 임계 미만 대기 제외, **묵은 간선 없음**(이번 틱만) |
| DL-02 | 결정성 | 같은 입력 → 같은 순환·양보·회피 지점(요청 순서 섞어도 동일) |
| DL-03 | 양보 비용 | 비용 최소, 동점 robotId, 우선순위 0 분모 방지 |
| DL-04 | `YieldSearch.Find` | 최근접 유효 칸, **경로 칸 미점유**, 점유·예약 제외, 상한 초과 → 확대 → 없음(failed, searched=상한) |
| DL-05 | 해소 락 | 같은 cycleKey 재탐지 시 저장 결정(IsNew=false), 양보 표시 로봇 낀 순환 건너뜀, TTL 만료 후 재해소, failed도 TTL 유지 |
| DL-06 | `DeadlockOptions.Validate` | TTL ≥ 거리×10, 위반 예외 |
| DL-07 | 해소 E2E(회피 포켓 통로) | 정면 대치 **해소**: 양보 로봇 우회→양쪽 완료, DeadlockDetected(yield, searchedDistance, resolvedMs>0, 1회), PathReplanned(deadlock) |
| DL-08 | 해소 실패(1칸 막다른 통로 `1111`) | 회피 지점 없음 → **순환 전원** TaskFailed(deadlock_unresolved)·Waiting→Error, DeadlockDetected(failed, searched=상한, resolvedMs=0, 1회) |
| DL-09 | 핑퐁·기아 없음(3대 행렬 대 1대) | 유한 틱 내 모두 종료 또는 상한 초과 실패. 같은 로봇이 무한 양보하지 않음(deadlock 재계획 상한 포함) |
| DL-10 | 재계획 순서 | 대안 있으면 PathReplanned(blocked)·쿨다운 억제·상한 초과 TaskFailed(replan_exceeded); **정면 대치는 replan_exceeded가 아니라 교착 해소로 간다**(대안 없어 재계획 미계산→1000ms 교착) |
| DL-11 | 이벤트 | Deadlock·Replan 배치 순서(rank 0), DeadlockDetected JSON 왕복에 searchedDistance, IsNew로 중복 발행 없음 |
| DL-12 | 적재 매핑 | `StoreMapping.ToConflict`가 searched_distance 채움(교착), ConflictDetected는 NULL |
| **교체** | 기존 `HeadOn_InCorridor_BothStayWaiting` | 삭제 → DL-07(해소)·DL-08(실패)로 대체 |

통합(실서버, skip): C-03 계약에 교착 적재 1건(searched_distance 왕복) 추가 — 접속 정보 있을 때.

---

## 12. 작업 분할

`[핵심]` = 중간 관리자. `[단순 S1~S4]` = 하위(Sonnet), 파일 겹침 없음. 두 단계로 간다.

### 1단계 — [핵심] 껍데기 커밋 (먼저)

S1~S4가 참조할 **시그니처를 먼저 커밋**한다(10 §13 2차 패턴). 본문은 `NotImplementedException`(또는 빈 반환)이고, `searched_distance` 적재 체인만 상관성이 커 통째로 실제 구현한다. 1단계 완료 조건 = build 경고 0·test 실패 0·format 통과.

| 껍데기 | 내용 |
|---|---|
| `Arbitration/MoveDecision.cs` | `DeadlockPhase` enum, `DeadlockInfo` record, `MoveDecision.Deadlock` 선택 필드(기본 null) — 타입 완결, 본문 없음 |
| `Arbitration/IMoveArbiter.cs` + `InMemoryMoveArbiter`·`AlwaysGrantMoveArbiter` | `DrainPendingDeadlocks(mapId, tick)` 시그니처 + **빈 목록** 반환(2단계에서 실제 구현). `DecideAsync`는 1단계에서 교착 미연결(그대로) |
| `Arbitration/DeadlockOptions.cs` | record + 기본값 + `Validate()` → throw (S1이 본문·테스트) |
| `Arbitration/DeadlockGraph.cs`·`YieldSearch.cs` | `FindCycles`·`Find` 시그니처 → throw (2단계 [핵심] 본문·테스트) |
| `Arbitration/DeadlockResolver.cs` | 클래스 껍데기(옵션 보관) — 2단계 세부 확정 |
| `Shared/Messaging/Payloads.cs` | `DeadlockDetectedPayload`에 `int SearchedDistance`(YieldToY 다음, ResolvedMs 앞 — 03 JSON 순서) |
| `Simulator/Events/EventCollector.cs` | `Deadlock(robotId, payload)`·`Replan(robotId, payload)` 시그니처 → throw (S2가 본문·테스트) |
| **searched_distance 체인(실구현)** | `Shared/Pipeline/StoreMapping.cs`(`ConflictLogChange.SearchedDistance` + `ToConflict` 채움, ConflictDetected는 NULL), `Infrastructure.Postgres/PgEventStore.cs`(conflict_log INSERT에 `searched_distance`), `tests/Shared.Tests/Pipeline/ConflictMappingTests.cs`(생성 인자 보정 + searched 단언) |

### 2단계 — [핵심] 본문 + [단순 S1~S4] 병렬 (메인 지시 후)

| 묶음 | 만들/고칠 파일 | 내용 | 완료 조건·테스트 |
|---|---|---|---|
| **[핵심]** | `DeadlockResolver.cs`·`DeadlockGraph.cs`·`YieldSearch.cs` 본문, `InMemoryMoveArbiter.cs`(판정 안 탐지·락·완료 감지·Drain), `RobotStepper.cs`·`Fleet/TaskLifecycle.cs`·`Fleet/FleetSimulation.cs`(해소 적용·재계획·flush·resolvedMs) | 탐지·양보·BFS·해소·재계획·drain | DL-01~05·07~10·12, build·test·format |
| **[S1]** | `Arbitration/DeadlockOptions.cs`(`Validate` 본문만), `Fleet/FleetOptions.cs`(재계획 키 3개 추가), **신규** `tests/FleetController.Tests/DeadlockOptionsTests.cs` | `Validate`: `TTL >= YieldSearchMaxDistance × 10` 아니면 `ArgumentException`(04 연동 제약). FleetOptions에 `ReplanWaitThresholdMs=500`·`ReplanCooldownMs=100`·`ReplanMaxPerTask=5` init 속성 | DL-06. 기본값 04 표 그대로 |
| **[S2]** | `Simulator/Events/EventCollector.cs`(`Deadlock`·`Replan` 본문), **신규** `tests/Simulator.Tests/EventCollectorDeadlockTests.cs` | `Deadlock`·`Replan`을 기존 `Conflict`처럼 `Add(robotId, 0, Item(...))` 로. DeadlockDetected는 `EventType.DeadlockDetected`, PathReplanned는 `EventType.PathReplanned` | DL-11(배치 rank 0 순서) |
| **[S3]** | **신규** `tests/Shared.Tests/Messaging/DeadlockPayloadJsonTests.cs`, `FleetJsonContext.cs` 확인 | `DeadlockDetectedPayload`(SearchedDistance 포함) JSON 왕복 = 03 예시, camelCase, 속성 순서 = 선언 순서(searchedDistance가 resolvedMs 앞) | DL-11(JSON 부분) |
| **[S4]** | **신규** 테스트 맵 또는 `TestMaps` 헬퍼(회피 포켓 통로 + 1칸 막다른 통로), `tests/Simulator.Tests` 픽스처, 기존 `MultiRobotTests.HeadOn_InCorridor_BothStayWaiting` **삭제** 후 DL-07·DL-08 **뼈대**(arrange + 단언은 [핵심] 병합 후 채움) | 해소/실패 시나리오 자산 | DL-07·DL-08 뼈대(단언 TODO) |

### S1~S4가 건드리지 말 파일 ([핵심] 전용)

`Arbitration/DeadlockResolver.cs`·`DeadlockGraph.cs`·`YieldSearch.cs`·`IMoveArbiter.cs`·`InMemoryMoveArbiter.cs`·`AlwaysGrantMoveArbiter.cs`·`MoveDecision.cs`·`MovePriority.cs`, `Simulator/RobotStepper.cs`·`Fleet/TaskLifecycle.cs`·`Fleet/FleetSimulation.cs`·`Fleet/TaskDispatcher.cs`·`Fleet/TaskGenerator.cs`, `Shared/Pipeline/StoreMapping.cs`, `Shared/Messaging/Payloads.cs`(SearchedDistance 1단계 확정 — 수정 금지), `Infrastructure.Postgres/PgEventStore.cs`, `tests/Shared.Tests/Pipeline/ConflictMappingTests.cs`.

> S1은 `DeadlockOptions.cs`의 `Validate` **본문만**, S2는 `EventCollector.cs`의 두 메서드 **본문만** 채운다(1단계 껍데기가 그 파일을 이미 만들어 둠). S1·S2가 손대는 그 두 파일을 [핵심] 2단계는 건드리지 않는다 — 파일이 겹치지 않는다.

---

## 13. 안전성 불변식 점검

| 불변식 | 영향 | 결론 |
|---|---|---|
| 2칸 선점 | 양보는 판정 경로로 회피 지점을 새 목표로 요청 — 선점 그대로 | 불변 |
| 칸 점유 만료 없음 | 해소 락·양보 표시만 TTL(틱), 칸 점유는 TTL 없음 | 불변 |
| run 전환 맵 해시 초기화+epoch | 교착 상태(락·표시)도 run 경계 초기화 대상 | 04 그대로(M4에서 `dl`·`yl` 포함) |
