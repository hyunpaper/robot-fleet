# 13. M2 상세 설계 — 교착 탐지와 해소

04 "교착 탐지와 해소"의 큰 틀·규칙 아래 M2 잔여(탐지·양보·재계획·이벤트)를 구현 단위로 채운다.
10과 같은 톤(표 + 근거 한 줄 + `>` 강조)으로 값마다 근거를 붙인다. **04 기본값·안전성 불변식은 바꾸지 않는다.**

---

## 1. 범위와 현재 상태

| 구성 | 현재 | M2에 필요한 것 |
|---|---|---|
| `InMemoryMoveArbiter` | 2칸 선점·점유 판정, 거부 시 `BlockedBy`로 대기 관계 산출(실제 판정 로직). `MoveDenyReason.Deadlock`·`MoveDecision.YieldTo`는 **자리만** 있고 미사용 | 대기 그래프 순환 탐지 + 양보 결정 |
| 1칸 통로 정면 대치 | 영구 `Waiting`(`HeadOn_InCorridor_BothStayWaiting` 고정) | 회피 지점이 있으면 해소, 없으면 `failed` |
| `DeadlockDetectedPayload` | `Cycle·Cells·Resolution·YieldRobotId·YieldToX/Y·ResolvedMs` | **`SearchedDistance` 없음** — 추가 필요(03은 이미 문서화) |
| `conflict_log.searched_distance` | DDL(001)에 컬럼 있으나 `StoreMapping`·`PgEventStore`가 **안 씀** | 적재 경로에 연결 |
| `PathReplanned` | Simulator가 **발행하지 않음** → `replan_log` 실데이터 없음 | 재계획 트리거에서 발행 |
| 재계획 트리거·쿨다운·상한 | 없음(대기만) | 04 "재계획 트리거"·"폭주 방지" 구현 |

구현 순서는 04 "구현 순서" 6~8단계(교착 탐지 → 양보 → 재계획 정책)다.

---

## 2. 경계 — 탐지·해소는 어디서 하나

> **판정(어느 로봇이 양보하고 어디로)은 중재기, 실행(이동 명령·재계획·이벤트)은 Simulator.** M4에서 중재기가 Redis 판정기로 바뀌어도 이 경계가 그대로 이어진다.

| 책임 | 주체 | 근거 |
|---|---|---|
| 대기 그래프·순환 탐지, 양보 로봇·회피 지점 선정, 해소 락·양보 표시 | **중재기**(`IMoveArbiter`) | 점유·예약 상태의 주인이고, 04/08 §4에서 M4는 이것을 **커밋 Lua**(맵 해시 `wg`·`dl`·`yl`)에서 한다. 판정과 같은 배치 안이라 결정적이다 |
| 양보 로봇 이동 명령·나머지 재계획, `DeadlockDetected`·`PathReplanned` 발행, `TaskFailed`, resolvedMs 측정 | **Simulator**(`TaskLifecycle`·`FleetSimulation`) | 로봇의 작업·경로 생애주기를 쥐고 있다. 이벤트는 03대로 "응답을 이벤트로 변환"하는 Simulator 몫 |

### 중재기 인터페이스 확장 (DecideAsync는 그대로)

기존 `DecideAsync`(판정) 시그니처는 **건드리지 않는다**(회귀 방지). 같은 틱 안에서 판정 **직후** 부르는 메서드를 하나 더 둔다.

```csharp
namespace RobotFleet.FleetController.Arbitration;

/// 거부된 로봇의 한 틱 스냅숏. RemainingPath = 현재 칸 이후 계획 경로(회피 지점이 남의 경로가 아닌지 검사용)
public readonly record struct WaitingRobot(
    string RobotId, Point Cell, string BlockedBy, int WaitMs, int TaskPriority, IReadOnlyList<Point> RemainingPath);

/// 한 순환의 해소 결정. Cycle·Cells는 이벤트용(발견 순서), 나머지는 실행·이벤트용
public readonly record struct DeadlockResolution(
    IReadOnlyList<string> Cycle, IReadOnlyList<Point> Cells, string Resolution,   // "yield" | "failed"
    string? YieldRobotId, Point? YieldTo, int SearchedDistance);

public interface IMoveArbiter   // 기존 두 메서드에 추가
{
    ValueTask<IReadOnlyList<DeadlockResolution>> ResolveDeadlocksAsync(
        string mapId, long tick, IReadOnlyList<WaitingRobot> waiting, DeadlockOptions options, CancellationToken ct = default);
}
```

| 결정 | 근거 |
|---|---|
| `AlwaysGrantMoveArbiter`(M1)·필요 시 다른 구현은 **빈 목록** 반환 | M1 회귀 없음. 교착은 점유 판정이 있을 때만 난다 |
| 스냅숏으로 `RemainingPath`를 넘긴다(중재기가 전체 경로를 들고 있지 않다) | 회피 지점 "남의 계획 경로 아님" 검사에 필요. **M4 Redis는 2칸 선점만 알아** `RemainingPath`가 비므로, 그때는 점유·예약 칸 기준으로만 거른다(§4 술어) |
| 해소 락·양보 표시 상태는 중재기 안(InMemory → M4 Redis `dl`·`yl`) | 판정과 같은 곳. 재탐지 방지(§5)가 판정 경로에서 결정적 |
| `RobotStepper.StepAllAsync`가 `DecideAsync` 직후 거부분을 모아 `ResolveDeadlocksAsync` 호출, 결과를 관찰자(`IRobotObserver`)로 Simulator에 전달 | 이벤트·실행은 Simulator. 판정은 틱 안에서 한 번 |

---

## 3. 대기 그래프와 순환 탐지

### 그래프

거부 결정에서 간선을 만든다: `robotId → BlockedBy`(점유 owner 또는 swap 상대). **거부된 로봇마다 나가는 간선이 정확히 하나**(요청 칸 1개 → 막은 로봇 1개)라 그래프는 **함수형(out-degree ≤ 1)**이다.

| 성질 | 결과 |
|---|---|
| out-degree ≤ 1 | 순환은 서로 꼭짓점을 공유하지 않는다(**서로소**). 04의 "중첩 순환"은 이 모델에선 생기지 않는다 — 한 틱에 여러 순환은 **독립된 ρ 모양**이라 각각 따로 푼다 |
| 막은 로봇이 대기 아님(Idle·체류 중) | 체인이 거기서 끝난다 → 순환 아님(추종 지연) |

### 탐지 시작 조건·알고리즘

```
탐지 시작: WaitMs >= deadlock_detect_wait_ms 인 거부 로봇만(04). 짧은 대기는 곧 풀리므로 보지 않는다
순환 찾기 : 그 로봇에서 BlockedBy를 따라가며 방문 집합에 다시 나오면 순환(04 DetectCycle). 함수형이라 O(체인 길이), 보통 2~4
```

| 결정 | 근거 |
|---|---|
| 시작 로봇을 **판정 정렬 순서**(점수 내림차순, 동점 robotId 오름차순)로 훑는다 | 같은 입력 → 같은 순환·같은 양보. 결정성(§9) |
| 한 틱에 **서로소 순환 여러 개**를 해소할 수 있다. 단 이미 이번 틱에 양보 표시된 로봇이 낀 순환은 건너뛴다 | 독립 순환은 서로 영향 없음. 겹치면 04의 양보 표시 규칙으로 재해소 방지 |
| 순환의 `Cycle`(이벤트)은 **탐지 시작 로봇부터의 방문 순서**, `cycleKey`(락)는 **robotId 정렬** | 03 이벤트는 발견 순서, 락 키는 어느 로봇에서 시작하든 같아야 한다(04) |

순수 함수로 분리해 테스트한다.

```csharp
public static class DeadlockGraph
{
    /// waitEdges: robotId → blockedBy, waitMs: robotId → 누적 대기ms.
    /// 반환: 서로소 순환들(각 순환은 임계 이상 대기 로봇을 하나 이상 포함), 결정적 순서
    public static IReadOnlyList<IReadOnlyList<string>> FindCycles(
        IReadOnlyDictionary<string, string> waitEdges, IReadOnlyDictionary<string, int> waitMs, int thresholdMs);
}
```

---

## 4. 양보 로봇 선정 · 회피 지점 · 실패

### 양보 로봇 선정 (04 비용식, 기본값 불변)

```
yieldCost = yield_cost.distance_weight × (회피 지점까지 거리)
          + yield_cost.priority_weight × (1 / 작업 우선순위)      // 우선순위 0이면 1로 본다(0분모 방지)
```

순환의 각 로봇에 대해 회피 지점 BFS로 **거리**를 구하고 비용 최소를 고른다. 동점은 **robotId 오름차순**(결정성). 회피 지점을 하나도 못 찾은 로봇은 후보에서 뺀다.

### 회피 지점 BFS

현재 칸에서 4방향 BFS, 가장 가까운 유효 칸. 유효 = **통행 가능 + 미점유 + 어느 로봇의 예약 칸(현재·목표)도 아님 + 남의 계획 경로 칸이 아님**.

| 항목 | 결정 | 근거 |
|---|---|---|
| "남의 계획 경로" | M2 단일 프로세스는 `RemainingPath`로 실제 검사(04 문구 그대로). **M4 Redis는 2칸 선점만 알아 예약 칸만으로 거른다** | 양쪽이 같은 `IYieldReservation` 술어를 쓰되 출처가 다르다. 04의 "계획 경로" 조건을 아는 정보로 구현 — 04 대비 명시 변경점(§10) |
| 상한 | `yield_search_max_distance`(기본 5) 홉까지 | 04 |
| 범위 확대 | 상한에서 못 찾으면 **+2**까지만 한 번 늘려 재탐색(04 파생 제약: TTL ≥ 거리×10이라 거리는 한 번에 +2) | 04 "## 설정값" 파생 제약 |
| 실패 | 확대 후에도 없으면 `failed`. `SearchedDistance` = 마지막 탐색 상한(03: 상한과 같으면 상한 걸림) | 04 "맵 설계 문제 신호", 03 `no_yield_point` 입력 |

```csharp
/// 반환: (회피 지점, 실제 탐색 홉). 못 찾으면 YieldPoint=null, Searched=마지막 상한.
/// reserved(cell): 점유·예약·남의 경로면 true. walkable(cell): 맵 통행 가능
public static class YieldSearch
{
    public static (Point? YieldPoint, int Searched) Find(
        GridMap map, Point from, int maxDistance, Func<Point, bool> reserved);
}
```

---

## 5. 해소 절차 · 락 · resolvedMs

### 절차 (04 "해소 절차"의 단일 프로세스 판)

```
1. 탐지(대기 임계 초과 + 순환) — ResolveDeadlocksAsync 안, 배치 판정 직후
2. 해소 락 cycleKey로 획득(메모리). 이미 있으면 저장된 결정을 그대로 반환(새 결정 안 함)
3. 양보 로봇 선정 + 양보 표시(yield mark), 락 값에 기록
4. Simulator: 양보 로봇에 회피 지점으로 가는 우회 leg 부여(status Moving, reason "yield"), 나머지는 대기 유지
5. 양보 로봇이 회피 지점 도착 → 막힌 칸이 풀려 나머지가 진행(path_cleared) 또는 재계획(reason deadlock)
6. 양보 로봇도 회피 지점에서 원래 목적지로 재계획(PathReplanned reason deadlock)
```

### 해소 락 (M2 = 메모리, TTL 틱 단위)

| 항목 | 결정 | 근거 |
|---|---|---|
| 키 | `cycleKey` = 순환 robotId 정렬 `'|'` 결합 | 04. 시작 로봇 무관하게 같은 키 |
| 값 | `tick·yieldRobotId·yieldTo·searchedDistance·resolution` | 두 번째 발견자가 같은 결정 재사용 |
| TTL | `deadlock_resolve_ttl_ticks`(기본 50틱), **락·양보 표시 모두** | 04. 후퇴 최대 5칸×10틱. 안 풀리면 만료 후 재탐지 |
| 해제 | 양보 로봇이 회피 지점 도착 시 삭제 | 04. 같은 로봇이 다시 교착이면 새 해소 가능 |
| 재탐지 방지 | 양보 표시된 로봇이 낀 순환은 새 해소를 시작하지 않는다 | 04. 부분집합 순환 중복 해소 방지 |

> **M2는 단일 프로세스라 락 경쟁이 사실상 없다**(04 블록 인용: 한 틱 배치는 한 주체가 통째). 그래도 락을 두는 이유는 **TTL 기반 재탐지 억제**와 **M4 Redis 락으로 코드가 그대로 이어지게** 하기 위함이다. 메모리 구현은 `Dictionary<cycleKey, (만료틱, 결정)>` 하나다.

### resolvedMs 정의와 이벤트 시점

| 경우 | DeadlockDetected 발행 시점 | resolvedMs |
|---|---|---|
| `yield` | **양보 로봇이 회피 지점에 도착한 틱**(해소 완료) | `(도착틱 − 탐지틱) × tickMs` — 실제 해소 시간 |
| `failed` | **탐지 틱**(회피 지점 없음이 그 자리에서 확정) | 0 |

> `resolvedMs`를 완료 시점의 실측으로 정의하므로(03 예시 1200 = 과거형), yield는 완료까지 pending으로 들고 있다가 발행한다. `searched_distance`·`cycle`·`cells`는 탐지 때 확정해 보관. `failed`는 즉시.

---

## 6. 재계획 트리거 · 폭주 방지 (Simulator)

04 "재계획 트리거"·"폭주 방지"를 `TaskLifecycle`에 둔다(로봇 작업·경로를 쥔 곳).

| 트리거 | 조건 | reason |
|---|---|---|
| 대기 초과 | 누적 WaitMs > `replan_wait_threshold_ms`(기본 500) | `blocked` |
| 교착 양보 | §5 4·6단계 | `deadlock` |
| 맵 변경 | (M2 범위 밖, 자리만) | `map_changed` |

| 폭주 방지 | 값 | 처리 |
|---|---|---|
| 로봇당 쿨다운 | `replan_cooldown_ms`(기본 100) | 직전 재계획 후 쿨다운 내면 재계획 안 함(대기 유지) |
| 작업당 상한 | `replan_max_per_task`(기본 5) | 초과 시 `TaskFailed`(reason `replan_exhausted`), 작업 실패 집계 |

재계획 = `planner.FindPath(현재→leg 목적지)` → `PlannedPath` 교체·`PathIndex=0`. `PathReplanned` 발행: `oldLength`=남은 옛 경로, `newLength`=새 경로, `computeMs`=`PathResult.Elapsed`. 대기→이동 전이 사유는 03대로 `replanned`(우회) 또는 `yield`(양보 이동 시작).

> **대기 즉시 재계획하지 않는다**(04). 임계·쿨다운·상한이 모두 이 이유(혼잡 이전·CPU)를 지킨다.

---

## 7. 이벤트 · 파이프라인 반영

### 발행 (EventCollector)

| 이벤트 | 추가 | 배치 순서(rank) |
|---|---|---|
| `DeadlockDetected` | `EventCollector.Deadlock(robotId, payload)`. **`SearchedDistance` 포함**, `resolution` ∈ {yield, failed} | 로봇 묶음 rank 0(충돌과 같은 묶음) |
| `PathReplanned` | `EventCollector.Replan(robotId, payload)` | rank 0 |

### 스키마·적재 (searched_distance 연결)

| 파일 | 변경 |
|---|---|
| `src/Shared/Messaging/Payloads.cs` | `DeadlockDetectedPayload`에 `int SearchedDistance` 추가(03 JSON에 이미 있음). 소스 생성 컨텍스트 자동 반영 |
| `src/Shared/Pipeline/StoreMapping.cs` | `ConflictLogChange`에 `short? SearchedDistance` 추가, `ToConflict`가 교착에서 채움 |
| `src/Infrastructure.Postgres/PgEventStore.cs` | `conflict_log` INSERT 컬럼·파라미터에 `searched_distance` 추가(DDL에 이미 있음) |

> 03·01·DDL은 이미 `searchedDistance`·`searched_distance`를 담고 있다. **코드만 비어 있던 것**이라 03·01은 바꾸지 않는다. ConflictDetected(비교착)의 `searched_distance`는 NULL.

---

## 8. 설정 옵션 (교착 키 옵션화)

전면 외부화는 M4 직전(04)이지만, 교착·재계획 키는 **지금 옵션 객체**로 받는다(상수 금지). 기본값 = 04 "## 설정값" 그대로.

```csharp
namespace RobotFleet.FleetController.Arbitration;

/// 04 설정값 9~13(교착). 기본값·허용 범위·연동 제약은 04 "## 설정값"
public sealed record DeadlockOptions
{
    public int DeadlockDetectWaitMs { get; init; } = 1000;      // 9
    public int YieldSearchMaxDistance { get; init; } = 5;       // 10
    public double YieldDistanceWeight { get; init; } = 1.0;     // 11
    public double YieldPriorityWeight { get; init; } = 1.0;     // 12
    public int DeadlockResolveTtlTicks { get; init; } = 50;     // 13

    /// 연동 제약: TTL >= 탐색거리 × 10 (04). 어기면 ArgumentException
    public void Validate();
}
```

재계획 키 6~8(`replan_wait_threshold_ms`·`replan_cooldown_ms`·`replan_max_per_task`)은 Simulator 쪽이라 `FleetOptions`에 더한다(기본 500·100·5). 설정 파일·환경변수 바인딩은 M4 직전 — 지금은 객체 기본값 + 테스트 주입만.

---

## 9. 결정성 규칙 요약

같은 입력(점유 상태·요청·대기시간·옵션) → 같은 출력이어야 한다(배치 재전송·M4 재사용의 전제).

| 지점 | 규칙 |
|---|---|
| 탐지 시작 순서 | 판정 점수 내림차순, 동점 robotId 오름차순 |
| 양보 로봇 동점 | robotId 오름차순 |
| 회피 지점 BFS 동점 | 같은 거리면 방문 순서 고정(방향 순서 N·E·S·W, 큐 FIFO) |
| 락 키 | robotId 정렬 결합(발견 순서 무관) |
| 부동소수 | 비용 비교만(정렬), 저장 없음. 가중치는 옵션 값 그대로 |

---

## 10. 04 · 03 대비 변경점

| 위치 | 04/03 원문 | 13 구현 | 근거 |
|---|---|---|---|
| 회피 지점 조건 | 04 "다른 로봇의 계획 경로가 아님" | M2는 `RemainingPath`로 실검사, **M4는 점유·예약 칸으로 근사** | 2칸 선점 모델은 전체 경로를 Redis에 두지 않는다. 같은 술어, 다른 출처 |
| DeadlockDetected 발행 시점 | 03 미규정 | yield=완료 틱, failed=탐지 틱 | `resolvedMs`를 실측으로 정의 |
| `DeadlockDetectedPayload` | 03 JSON에 `searchedDistance` 있음 | **코드 record에 필드 추가**(없던 것) | 03과 코드 일치 |
| `searched_distance` 적재 | 01 DDL에 컬럼 있음 | 매핑·INSERT 연결 | 비어 있던 경로 |
| TaskFailed 사유 | 03 예시 `no_path` | `deadlock_unresolved`·`replan_exhausted` 추가 | 04 실패 처리 두 경로 |

> 03·01·04 **문서는 바꾸지 않는다**(기본값·스키마 이미 일치). 바뀐 것은 코드와, 위 두 가지 구현 판단(회피 지점 출처, 발행 시점)이다 — 총괄 리뷰 대상.

---

## 11. 테스트 (순수 로직 — 실제 판정이라 인프라 없이 돈다)

| ID | 대상 | 검증 |
|---|---|---|
| DL-01 | `DeadlockGraph.FindCycles` | 2-순환(정면), 3-순환, 추종(순환 아님), 서로소 2순환, 임계 미만 대기 제외 |
| DL-02 | 결정성 | 같은 입력 → 같은 순환·양보 로봇·회피 지점(요청 순서 섞어도 동일) |
| DL-03 | 양보 비용 | 비용 최소 선정, 동점 robotId, 우선순위 0 분모 방지 |
| DL-04 | `YieldSearch.Find` | 최근접 유효 칸, 점유·예약·남의 경로 제외, 상한 초과 → 확대 → 없음(failed, searched=상한) |
| DL-05 | 해소 락 | 같은 cycleKey 재탐지 시 저장 결정 반환, 양보 표시 로봇 낀 순환 건너뜀, TTL 만료 후 재해소 |
| DL-06 | `DeadlockOptions.Validate` | TTL ≥ 거리×10, 위반 예외 |
| DL-07 | 해소 E2E(통로+회피 포켓 맵) | 정면 대치 **해소**: 양보 로봇 우회→양쪽 완료, DeadlockDetected(yield, searchedDistance, resolvedMs>0), PathReplanned 발행 |
| DL-08 | 해소 실패(1칸 막다른 통로 `1111`) | 회피 지점 없음 → failed, TaskFailed(deadlock_unresolved), DeadlockDetected(failed, searched=상한) |
| DL-09 | 기아·핑퐁 없음 | 반복 해소가 무한 루프 안 됨(양보 표시+락), 유한 틱 내 완료 |
| DL-10 | 재계획 | 대기 초과 → PathReplanned(blocked), 쿨다운 억제, 상한 초과 → TaskFailed(replan_exhausted) |
| DL-11 | EventCollector | Deadlock·Replan 배치 순서(rank 0), DeadlockDetected JSON 왕복에 searchedDistance 포함 |
| DL-12 | 적재 매핑 | `StoreMapping.ToConflict`가 searched_distance 채움(교착), ConflictDetected는 NULL |
| **교체** | 기존 `HeadOn_InCorridor_BothStayWaiting` | 삭제 → DL-07(해소)·DL-08(실패)로 대체 |

통합(실서버, skip): C-03 계약에 교착 적재 1건 추가(searched_distance 포함 왕복) — 접속 정보 있을 때.

---

## 12. 작업 분할

`[핵심]` = 중간 관리자. `[단순 S1~S4]` = 하위(Sonnet), 파일 겹침 없음. 선행이 있는 묶음은 10 §13처럼 **시그니처 확정 껍데기**에 맞춰 쓴다.

| 묶음 | 파일 | 내용 | 선행 |
|---|---|---|---|
| **[핵심]** | `src/FleetController/Arbitration/DeadlockResolver.cs`(신규), `DeadlockGraph.cs`·`YieldSearch.cs`(신규 순수), `IMoveArbiter.cs`·`InMemoryMoveArbiter.cs`·`AlwaysGrantMoveArbiter.cs`(ResolveDeadlocksAsync), `src/Simulator/RobotStepper.cs`·`Fleet/TaskLifecycle.cs`·`Fleet/FleetSimulation.cs`(해소 적용·재계획·resolvedMs), `src/Shared/Pipeline/StoreMapping.cs`·`src/Infrastructure.Postgres/PgEventStore.cs`(searched_distance 연결), 핵심 테스트 DL-01~05·07~10·12 | 탐지·양보·BFS·해소·재계획·적재 연결 | S1·S2 껍데기 |
| **[S1]** | `src/Shared/Messaging/Payloads.cs`(DeadlockDetectedPayload에 `SearchedDistance`), `FleetJsonContext.cs` 확인, `tests/Shared.Tests/Messaging/`(JSON 왕복 DL-11 일부) | payload 한 필드 + 직렬화 테스트 | 없음 |
| **[S2]** | `src/FleetController/Arbitration/DeadlockOptions.cs`(신규) + `Validate`, `src/Simulator/Fleet/FleetOptions.cs`(재계획 키 3개), 옵션 테스트 DL-06 | 옵션 record + 검증 | 없음 |
| **[S3]** | `src/Simulator/Events/EventCollector.cs`(`Deadlock`·`Replan` 발행 helper), 배치 순서 테스트 DL-11 일부 | 발행 helper 2개 | S1 껍데기 |
| **[S4]** | 테스트 맵 `maps/*.json`(회피 포켓 통로) 또는 `tests/.../TestMaps` 헬퍼, `tests/Simulator.Tests` 픽스처, 기존 HeadOn 테스트 교체 뼈대 | 테스트 자산 | 없음 |

> 선행 처리: [핵심]은 S1(`SearchedDistance`)·S2(`DeadlockOptions`) 시그니처에 의존한다. 10 §13처럼 **껍데기(필드·record·메서드 시그니처)를 먼저 커밋**하면 네 묶음이 병렬로 컴파일된다. `searched_distance` 적재 체인(payload→매핑→INSERT)은 상관성이 커 [핵심]이 통째로 쥔다 — S1은 payload 필드만.

---

## 13. 안전성 불변식 점검

| 불변식 | 영향 | 결론 |
|---|---|---|
| 2칸 선점 | 양보는 **판정 경로로** 회피 지점을 새 목표로 요청 — 선점 규칙 그대로 탄다 | 불변 |
| 칸 점유 만료 없음 | 해소 락·양보 표시만 TTL(틱), **칸 점유는 TTL 없음** | 불변 |
| run 전환 맵 해시 초기화+epoch | 교착 상태(락·표시)도 run 경계에서 초기화 대상 | 04 그대로(M4 구현 시 `dl`·`yl` 포함) |
