# Robot Fleet

공장 물류 로봇(AMR) 관제 시스템. 경로 계획, 충돌 회피, 교착 해소를 처리하고
운행 이벤트를 모아 "이 맵에서 로봇은 몇 대가 적당한가"를 수치로 답하는 것이 목표다.

가상 공장 맵 위에서 시뮬레이터가 로봇을 움직이고, 관제 서버가 매 틱 이동을 승인한다.
실제 장비 대신 시뮬레이터를 데이터 소스로 쓰기 때문에 로봇 수와 틱 주기만 바꿔서 부하를 자유롭게 조절할 수 있다.

![3D 시뮬레이션](docs/images/fleet-3d.gif)

<details>
<summary>2D 탑뷰</summary>

![2D 시뮬레이션](docs/images/fleet-2d.gif)

</details>

로봇이 지나간 칸은 그 로봇의 색으로 칠해진다. 회색 블록은 설비, 바닥색이 다른 칸은 스테이션·충전소·주차 구역이다.

---

## 현재 상태

| 단계 | 내용 | 상태 |
|---|---|---|
| M1 | 맵 로드·검증, A* 경로 탐색(회전 비용 포함), 단일 로봇 시뮬레이션 | 완료 |
| M2 | 다중 로봇, 충돌 회피, 교착 탐지·해소 | 진행 예정 |
| M3 | Ingest API + RabbitMQ + Worker로 이벤트 파이프라인 분리 | |
| M4 | 락·상태·멱등 키를 Redis로 이전, 관제 서버 다중 인스턴스 | |
| M5 | k3s 배포, nginx ingress, 스케일 아웃 | |
| M6 | Prometheus + Grafana, 실시간 관제 화면 | |
| M7 | 실험: 로봇 수별 처리량, 회피 전략·우선순위 정책 비교 | |
| M8 | 장애 주입: pod 강제 종료, 브로커 재시작, 네트워크 지연 | |

> 위 GIF는 M1 단계 결과물이다. 아직 충돌 회피가 없어서, 경로가 같은 시각에 겹치지 않도록 출발지와 목적지를 골라 돌렸다.

---

## 실행

.NET 8 SDK가 필요하다.

```bash
dotnet build
dotnet test

# 기본 시나리오: 주차 구역 (0,39) → 스테이션 (6,3)
dotnet run --project src/Simulator -- --map maps/factory-a.json --start 0,39 --goal 6,3

# 대기 없이 결과만 보기
dotnet run --project src/Simulator -- --map maps/factory-a.json --speed 0 --render 0
```

| 옵션 | 기본값 | 설명 |
|---|---|---|
| `--map` | (필수) | 맵 파일 경로 |
| `--start` / `--goal` | 첫 주차 구역 / 첫 스테이션 | `x,y` |
| `--heading` | `N` | 시작 방향 `N` `E` `S` `W` |
| `--speed` | `1.0` | 배속. `0`이면 대기 없이 실행 |
| `--tick` | `100` | 틱 길이(ms) |
| `--render` | `5` | 맵을 몇 틱마다 그릴지. `0`이면 생략 |
| `--max-ticks` | `100000` | 이 틱 수를 넘기면 종료 |

종료 코드는 `0` 도착, `2` 경로 없음, `3` 최대 틱 초과, `130` 중단(Ctrl+C)이다.

출력 예시:

```
[t=000001 0.1s] R-001 PLAN    (0,39)N → (6,3)   steps=42 cost=43.0 expanded=136 in 2.3ms
[t=000341 34.1s] R-001 TURN    N → E
[t=000344 34.4s] R-001 MOVE    (0,5) → (1,5)
[t=000404 40.4s] R-001 TURN    E → N
[t=000427 42.7s] R-001 ARRIVED (6,3)   ticks=427 moves=42 turns=2 overrun=0
```

### GIF 다시 만들기

GIF는 실제 시뮬레이터 로직(A*, RobotStepper)으로 뽑은 트레이스를 그린 것이다. 시나리오는 [tools/TraceExport/Scenario.cs](tools/TraceExport/Scenario.cs)에 있다.

```bash
dotnet run --project tools/TraceExport -- --map maps/factory-a.json --out artifacts/trace/factory-a.json
pip install -r tools/render/requirements.txt
python tools/render/render_gif.py artifacts/trace/factory-a.json docs/images/fleet-3d.gif --view iso
python tools/render/render_gif.py artifacts/trace/factory-a.json docs/images/fleet-2d.gif --view 2d
```

TraceExport는 같은 틱에 두 로봇이 같은 칸을 점유하면 exit 4로 끝난다.

---

## 구조

```
robot-fleet/
├── src/
│   ├── Shared/            좌표, 맵 로더·검증, 도메인 모델
│   ├── FleetController/   A* 경로 계획, 이동 승인
│   └── Simulator/         틱 루프, 로봇 이동, 콘솔 출력
├── tests/                 xUnit 테스트 (Shared / FleetController / Simulator)
├── tools/                 트레이스 내보내기, GIF 렌더러
├── maps/
│   ├── factory-a.json     60×40 공장 맵
│   └── test-corridor.json 9×7 테스트 맵 (1칸 통로)
└── docs/                  설계 문서
```

이후 단계에서 `IngestApi`, `EventWorker`, `QueryApi`, `deploy/`, `loadtest/`가 추가된다.

---

## 설계 요약

```
Simulator ──(틱마다 배치 이동 요청)──► FleetController ◄──► Redis   셀 락, 로봇 상태, 대기 그래프
    │
    └──(이벤트 배치)──► IngestApi ──► RabbitMQ ──► EventWorker ──► PostgreSQL
                                                                     ▲
                                                         QueryApi ───┘ ──► 관제 화면 / Grafana
```

- **이동 승인은 동기, 이벤트 적재는 비동기.** 로봇이 움직여도 되는지는 즉답이 필요하고, DB 적재가 느려져도 로봇은 멈추면 안 된다.
- **배치 이동 판단.** 로봇 200대가 각각 HTTP를 보내면 100ms 틱을 맞출 수 없어서 틱당 한 번에 묶어 판정한다.
- **분산 락 + 교착 해소.** 현재 칸과 다음 칸을 함께 잠가 자리 맞바꿈 충돌을 막고, 대기 그래프에서 순환을 찾아 한 대를 비켜 세운다.
- **순서 보장.** `robotId` 기준 consistent hash로 파티션을 나누고 파티션마다 consumer를 하나만 둔다.
- **멱등.** 이벤트 ID를 `{batchId}/{index}`로 고정해서 재전송해도 같은 값이 되게 하고, Ingest와 Worker 양쪽에서 거른다.
- **시간 분해.** 로봇의 시간을 이동, 작업, 대기, 유휴로 나눈다. 대기(길이 막힘)와 유휴(할 일 없음)를 구분해야 병목이 로봇 수인지 통로인지 알 수 있다.

자세한 내용은 문서 참고.

| 문서 | 내용 |
|---|---|
| [01-architecture](docs/01-architecture.md) | 서비스 분리, 데이터 흐름, 큐·Redis·DB 설계 |
| [02-infrastructure](docs/02-infrastructure.md) | k3s 구성, 리소스 배분, 모니터링, 장애 주입 |
| [03-map-and-events](docs/03-map-and-events.md) | 맵 포맷, 도메인 모델, 이벤트 스키마, 상태 전이 |
| [04-algorithm](docs/04-algorithm.md) | A*, 충돌 유형, 락 전략, 교착 탐지·해소 |
| [05-metrics](docs/05-metrics.md) | 지표 정의, 실험 설계 |
| [06-m1-design](docs/06-m1-design.md) | M1 상세 설계와 테스트 목록 |

---

## 기술 스택

| 구분 | 사용 |
|---|---|
| 언어 | C# / .NET 8 |
| API · 백그라운드 | ASP.NET Core Minimal API, Worker Service |
| 메시징 | RabbitMQ (MassTransit) |
| 상태 · 락 | Redis |
| 저장소 | PostgreSQL |
| 배포 | k3s, nginx ingress |
| 모니터링 | Prometheus, Grafana |
| 부하 테스트 | 시뮬레이터, k6 |

## 배포 환경 (예정)

자체 서버에 k3s 3노드로 구성한다. 아래 주소는 예시다.

| 노드 | 주소 | 역할 |
|---|---|---|
| node-1 | 192.168.10.11 | control plane, PostgreSQL, RabbitMQ |
| node-2 | 192.168.10.12 | 애플리케이션 |
| node-3 | 192.168.10.13 | 애플리케이션, 모니터링 |

접속 정보, 인증서, kubeconfig, `.env` 같은 설정 파일은 저장소에 올리지 않는다(`.gitignore` 참고).
