# Robot Fleet Management System

공장 내부를 돌아다니는 물류 로봇(AMR)의 **관제 · 충돌 회피 · 효율 분석** 시스템.

가상 공장 맵 위에서 로봇 수십~수백 대가 작업을 수행하고, 그 과정에서 발생하는
위치·상태·충돌 이벤트를 실시간으로 수집해 운영 지표로 환산한다.

---

## 왜 이 주제인가

| 관점 | 내용 |
|---|---|
| 데이터 공급 | 시뮬레이터가 곧 데이터 소스. 외부 의존 없이 무한 생성 |
| 부하 시험 | 로봇 수와 갱신 주기만 올리면 초당 수백 ~ 수만 건 |
| 알고리즘 깊이 | 경로 탐색, 다중 에이전트 충돌 회피, 교착 탐지 |
| 분산 시스템 | 순서 보장, 멱등, 분산 락, 수평 확장 |
| 결과 제시 | "로봇 몇 대가 최적인가"를 **실험 결과 그래프**로 증명 |

실제 AMR 도입 현장에서도 시뮬레이션으로 사전 검증한다. 합성 데이터라는 점이 약점이 되지 않는 소재.

---

## 핵심 기술 과제

1. **충돌 회피** — 같은 칸 진입, 자리 맞바꿈, 추종 충돌을 모두 처리
2. **교착 해소** — 순환 대기를 탐지하고 양보 로직으로 푼다
3. **이벤트 순서 보장** — 같은 로봇의 이벤트는 반드시 순서대로 처리
4. **멱등 처리** — 재전송된 이벤트가 지표를 두 번 더하지 않게
5. **수평 확장** — 관제·수집·처리를 각각 독립적으로 늘릴 수 있게
6. **효율 측정** — 손실 시간을 분해해 병목을 숫자로 지목

---

## 기술 스택

| 구분 | 선택 |
|---|---|
| 언어 / 런타임 | C# / .NET 8 |
| API | ASP.NET Core (Minimal API) |
| 백그라운드 | .NET Worker Service |
| 메시지 브로커 | RabbitMQ (MassTransit) |
| 캐시 / 분산 락 | Redis (StackExchange.Redis) |
| 데이터베이스 | PostgreSQL |
| 오브젝트 스토리지 | MinIO *(선택 — 맵 스냅샷·리플레이 저장용)* |
| 컨테이너 오케스트레이션 | k3s |
| 인그레스 | nginx ingress controller |
| 모니터링 | Prometheus + Grafana (`prometheus-net`) |
| 부하 테스트 | 시뮬레이터 자체 + k6 (API 부하용) |

---

## 문서

| 문서 | 내용 |
|---|---|
| [01-architecture.md](docs/01-architecture.md) | 서비스 분리, 데이터 흐름, 큐·Redis·DB 설계 |
| [02-infrastructure.md](docs/02-infrastructure.md) | k3s 구성, 배포, 리소스 배분, 모니터링 |
| [03-map-and-events.md](docs/03-map-and-events.md) | 맵 구조, 도메인 모델, 이벤트 스키마 |
| [04-algorithm.md](docs/04-algorithm.md) | 경로 탐색, 충돌 회피, 교착 처리 |
| [05-metrics.md](docs/05-metrics.md) | 지표 정의, 실험 설계 |
| [06-m1-design.md](docs/06-m1-design.md) | M1 상세 설계 — 프로젝트 구조, 타입 시그니처, A* 규약, 틱 루프, 샘플 맵, 테스트 목록, 작업 분할 |

---

## 로드맵

| 단계 | 범위 | 완료 기준 |
|---|---|---|
| **M1** | 맵 + 로봇 1대 + A* 경로 탐색 (단일 프로세스) | 콘솔에서 로봇이 목적지까지 이동 |
| **M2** | 다중 로봇 + 충돌 회피 + 교착 처리 | 로봇 20대가 막히지 않고 계속 작업 수행 |
| **M3** | 이벤트 외부 발행 — Ingest API + RabbitMQ + Worker 분리 | 이벤트가 DB에 적재되고 유실 0 |
| **M4** | Redis 이전 (락·상태·멱등), 관제 다중 pod | 관제 pod 2개가 동시에 떠도 충돌 없음 |
| **M5** | k3s 배포 + nginx + 스케일 아웃 | pod 수 조절로 처리량 변화 확인 |
| **M6** | Prometheus + Grafana + 실시간 관제 화면 | 맵 위 로봇 움직임과 지표 동시 확인 |
| **M7** | 실험 — 로봇 수별 처리량, 알고리즘 비교, 혼잡 히트맵 | 그래프 산출 |
| **M8** | 장애 주입 — pod kill, 브로커 다운, 지연 | 복구 동작 검증 |

M2까지만 완료해도 포트폴리오 항목 하나가 성립한다.

---

## 진행 상태

- [ ] M1 맵 + 단일 로봇 경로 탐색
- [ ] M2 다중 로봇 충돌 회피
- [ ] M3 이벤트 파이프라인 분리
- [ ] M4 Redis 상태 이전
- [ ] M5 k3s 배포
- [ ] M6 모니터링 · 관제 화면
- [ ] M7 실험 및 분석
- [ ] M8 장애 주입 검증

---

## 디렉터리 구조 (예정)

```
robot-fleet/
├── README.md
├── RobotFleet.sln
├── Directory.Build.props    # net8.0, Nullable, TreatWarningsAsErrors
├── docs/
├── src/
│   ├── Shared/              # 공통 모델 (좌표, 맵, 도메인), 이벤트 계약      — M1
│   ├── FleetController/     # 경로 계획, 이동 승인 (라이브러리)              — M1
│   ├── FleetController.Api/ # 위 라이브러리를 감싼 HTTP API                  — M3
│   ├── Simulator/           # 가상 공장 + 로봇 시뮬레이터 (콘솔)             — M1
│   ├── IngestApi/           # 이벤트 수신                                   — M3
│   ├── EventWorker/         # 이벤트 소비 · 적재 · 집계                     — M3
│   └── QueryApi/            # 조회 · 관제 화면 백엔드                       — M6
├── tests/
│   ├── Shared.Tests/
│   ├── FleetController.Tests/
│   └── Simulator.Tests/
├── deploy/
│   ├── k3s/                 # 매니페스트
│   └── monitoring/          # Prometheus, Grafana
├── loadtest/                # k6 시나리오
└── maps/
    ├── factory-a.json       # 60×40 기본 맵
    └── test-corridor.json   # 9×7 테스트 맵 (1칸 통로)
```
