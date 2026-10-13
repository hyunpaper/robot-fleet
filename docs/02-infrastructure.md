# 02. 인프라

## 환경

- **집 PC VMware VM 3대**(M5 · M6 · M8 · M9). **M7 부하 실험만 AWS**
- **매니지드 데이터 서비스(RDS · ElastiCache · Amazon MQ)를 쓰지 않는다.** 로컬 VM이든 EC2든 직접 운영한다 —
  매니지드를 쓰면 Sentinel 장애 조치 · quorum 재선출 · 해시 체인이 의미를 잃고 M5~M8이 사라진다
- 쿠버네티스 배포판: **k3s**
- 인그레스: nginx ingress controller (k3s 기본 Traefik 비활성화)

### 단계별 실행 환경

| 단계 | 어디서 | Redis | Sentinel | RabbitMQ | 비용 |
|---|---|---|---|---|---|
| **M3 · M4** | **k3s 불필요** — 사용자가 제공한 단일 인프라 서버(RabbitMQ · Redis · PostgreSQL). 접속 정보는 `appsettings.Local.json`(커밋 금지) 또는 환경 변수 | 1 | 없음 | 1 (quorum 타입, 멤버 1) | 0원 |
| **M5 · M6 · M8 · M9** | 집 PC — **VMware VM 3대 k3s** | 3 | 3 | 3 (멤버 3으로 `grow`) | 전기료 |
| **M6 추가** | 실시간 관제 화면 **+ run 재생 데이터 내보내기 · 정적 재생기**(아래 "외부 공개") | | | | 0원 |
| **M7 부하 실험** | **AWS** (아래 "M7 — AWS"). **run 전환 기능(`POST /admin/run`)이 선결**(아래 "run 관리") | 3 | 3 | 3 | 회당 $1~2 |
| **M9 교착 분석 에이전트** | 집 PC 호스트(클러스터 밖) — 로컬 LLM이 **정책 파라미터 수정안**을 근거와 함께 제시(04 "M9 교착 분석 에이전트의 액션 공간") | | | | 0원 |

> 인프라 서버에는 `rabbitmq_consistent_hash_exchange` 플러그인이 켜져 있어야 한다. 없으면 기동이 실패한다(10 §3).

| 왜 로컬이 기본인가 | 로컬 VMware | AWS |
|---|---|---|
| 세션 전환 | **suspend / resume** — 상태 그대로 | destroy → 재생성 → 덤프 복원 |
| IP | **고정** | 매번 변경 |
| 메모리 | **8GiB × 3**, 모니터링 상시 | 비용 때문에 빠듯 |
| M8 반복 실험 | **VM 스냅샷으로 같은 상태 재현** | 매번 재구축, 조건이 미세하게 달라짐 |
| CPU | **6코어 — M7의 제약** | 필요한 만큼 |

### 로컬 VM

| 항목 | 값 | 비고 |
|---|---|---|
| 호스트 | Ryzen 5 3500X **6코어 6스레드** · RAM **64GB** | VM에 6 vCPU — 여유 없음. RAM은 VM 24GB, 호스트 40GB |
| GPU | GTX 1660 Super 6GB | **VM에 넘기지 않는다** — M9 로컬 LLM은 호스트에서 |
| 디스크 | VM 100GB + 스냅샷 + suspend 파일 24GB | 200GB 이상 여유 |
| 하이퍼바이저 | VMware Workstation Pro | 스냅샷 · suspend. WSL2 · Docker Desktop을 켜면 Hyper-V가 켜져 성능이 떨어진다 |
| 네트워크 | **NAT(`vmnet8`) 하나**, DHCP 범위 밖 주소를 게스트에서 static — `192.168.10.11~13` | 대역은 `Virtual Network Editor`에서 확인해 맞춘다 |
| 세션 | 저녁 resume → 작업 → suspend. M8 장애 주입 전에는 **스냅샷** | 전원을 내릴 때는 node-2 · 3 → node-1 순 |
| 백업 | **VM 스냅샷 + 정기 `pg_dump`** | 인스턴스가 사라지지 않으므로 덤프 · 복원 스크립트가 필요 없다 |

### M7 — AWS

05 실험 1의 결론은 "이 맵에서 로봇 N대가 최적"인데, 호스트가 6코어뿐이라 틱 초과가 **알고리즘 한계인지 하드웨어 한계인지**
구분되지 않는다. 그 N이 맵 특성이 아니라 PC 사양이 된다. 나머지 단계는 CPU 부하가 아니라 구조와 동작을 보므로 로컬로 충분하고,
M8은 스냅샷 반복이 되는 로컬이 오히려 낫다.

| 항목 | 값 | 근거 |
|---|---|---|
| 인스턴스 | `c7g.xlarge` × 3 (4 vCPU · 8GiB) **온디맨드** | Spot 회수가 측정을 오염시키면 안 된다. 버스터블(`t4g`)은 CPU 크레딧에 따라 성능이 흔들린다 |
| 리전 · 배치 | 서울, 노드 3대 **같은 서브넷** | 노드 간 텔레메트리 전송이 무료인 범위에 둔다 |
| 운용 | `terraform apply` → 실험 → **`terraform destroy`** (`deploy/terraform/`, M7 전용) | EBS는 인스턴스를 꺼도 과금된다. 결과(덤프 · 보고서 · Grafana 스냅샷)를 먼저 회수 |
| 노드명 · 매니페스트 | 로컬과 **같은 매니페스트**, `--node-name node-1~3` 필수 | 노드 생성만 Terraform이 맡는다 |
| ⚠️ 선결 | 신규 계정 vCPU 한도 **5** — 12가 필요하므로 **Service Quotas 증액 신청**(서울, 온디맨드 · Spot 각각) | 수 시간~며칠 걸린다 |
| 크레딧 | $200 / 6개월. 구 프리티어(750h EC2 · 30GB EBS)는 없다 | M7 몇 회면 $10도 쓰지 않는다 |
| 환경 구분 | `simulations.environment` = `local` / `aws` (01 · 03) | 하드웨어가 바뀌면 다른 조건이다(05 "동일 조건에서 변수 하나만") |

### 외부 공개 — 재생기와 실시간

실시간 관제를 인터넷에 상시로 열지 않는다. **주력은 재생기**다.

| | 기록 재생 (주력) | 실시간 (데모용) |
|---|---|---|
| 형태 | run 종료 후 내보낸 데이터를 **GitHub Pages 정적 재생기**(`docs/viewer/`)로 | **Cloudflare Tunnel** — 발표 · 시연 때만 |
| 데이터 | QueryApi `GET /api/v1/runs/{runId}/replay`(M6)가 `simulations` + `robot_telemetry`를 압축 포맷으로 | WebSocket 스트림 |
| PC 전원 | 꺼져 있어도 된다, 24/7 | 켜져 있어야 한다 |
| 비용 · 보안 | 무료 · 클러스터 노출 없음 | 무료 · 아웃바운드 연결만, Cloudflare Access로 접근 제어 |

재생기 기능: 재생 · 일시정지 · 배속 · 틱 점프, **run 비교 재생**(`runId`가 전제), 교착 · 장애 주입 시점 북마크.
크기: 2분 · 로봇 200대 · 10틱/초면 약 1.4MB(초당 1틱 샘플링이면 140KB).

| 노출 금지 | 이유 |
|---|---|
| 6443 (kubectl) | 클러스터 전체 권한 |
| **Simulator `POST /admin/run`** | 로봇 수 · 배속을 아무나 바꿀 수 있다 |
| Grafana 편집 | 익명 읽기 전용으로 두거나 노출하지 않는다. 실험 보고서에는 스냅샷 · 스크린샷을 첨부 |
| RabbitMQ 관리 플러그인 · PostgreSQL · Redis | |

**QueryApi 조회 + WebSocket, nginx ingress 80/443만** 터널에 올린다.

### run 관리 — 로봇 수 변경과 배속

M7 실험(로봇 수 8종 × 3회 = 24 run)과 관제 화면 조작이 모두 **"새 run을 시작한다"**는 같은 동작이다.
배속은 이미 있다(`--speed`, `--speed 0` = 대기 없음, `--tick`, `ITickDelay` — 06).

```
POST /admin/run            (Simulator 관리 포트 — 인그레스 · 터널에 올리지 않는다, 10 D5)
{ "robots": 200, "speed": 1.0, "tickMs": 100, "taskSeed": 20261013 }
```

| 순서 | 동작 | 빠뜨리면 |
|---|---|---|
| 1 | 틱 루프 정지 | — |
| 2 | **송신 버퍼 Drain** | 이전 run 이벤트 **유실** |
| 3 | `SimulationEnded` 발행 (03) | `simulations` 종료 통계 공백 |
| 4 | **Redis 맵 해시 초기화 + epoch 증가** (04 · 08) | 이전 run이 점유한 칸이 남아 새 run이 즉시 교착 |
| 5 | 새 `runId` 발급 (03) | run이 섞인다 |
| 6 | `SimulationStarted` 발행 → 로봇 N대 생성 → 틱 루프 시작 | — |

| 결정 | 근거 |
|---|---|
| Simulator 관리 엔드포인트(채택) | EventWorker가 이미 관리 포트를 가진다(10 D5). pod 재시작 없이 전환하고 Drain을 깔끔히 처리한다. ConfigMap + `rollout restart`는 QueryApi에 클러스터 RBAC을 줘야 한다 |
| **선결: run 식별자(01 · 03 · 10)** | 먼저 넣으면 `robot_state`가 run을 못 가려 전체 로봇이 `valid=false`, `task_history`는 조용히 오염된다 |
| 선결: 04 설정값 외부화 | `POST /admin/run`이 `params`를 받아 `SimulationStarted`에 싣는다 |
| 구현 시점 | M6 또는 M7 진입. 그 전까지는 `--robots N` CLI 재시작으로 충분하다 |

---

## k3s 설치

노드 3대를 **동일 사양**으로 두고, node-1이 k3s server를 겸한다(**taint 없음**).

```bash
# node-1 (server) — taint 없음
curl -sfL https://get.k3s.io | sh -s - \
  --node-name node-1 \
  --disable traefik --disable servicelb \
  --write-kubeconfig-mode 644 \
  --tls-san 192.168.10.11 \
  --token "<k3s_token>" \
  --kubelet-arg=system-reserved=memory=600Mi

# node-2 · node-3 (agent)
curl -sfL https://get.k3s.io | K3S_URL=https://192.168.10.11:6443 \
  K3S_TOKEN=<k3s_token> sh -s - \
  --node-name node-2 \
  --kubelet-arg=system-reserved=memory=600Mi

kubectl label node node-1 fleet/pg=true
```

| 옵션 | 이유 |
|---|---|
| `--node-name node-1` / `node-2` / `node-3` | **빠뜨리면 게스트 호스트명이 노드명이 되어, VM을 다시 만들었을 때 local PV의 `nodeAffinity`가 맞지 않고 PV가 영원히 Pending된다** |
| `--disable traefik` | nginx ingress를 직접 설치 — 현업 환경과 맞춤 |
| `--disable servicelb` | MetalLB 또는 NodePort 사용 |
| `--write-kubeconfig-mode 644` | sudo 없이 kubectl 사용 |
| `--tls-san 192.168.10.11` | IP가 고정이라 한 줄이면 된다. kubeconfig도 한 번 받으면 계속 쓴다 |
| `system-reserved=memory=600Mi` | 노드별 allocatable 7.4Gi |

### 노드 구성

| 구성 | 조건 | 비고 |
|---|---|---|
| 단일 노드 | 서버 1대 | **M3 · M4는 k3s 자체가 불필요** — 사용자가 제공한 단일 인프라 서버(RabbitMQ · Redis · PostgreSQL), 접속 정보는 `appsettings.Local.json`(커밋 금지) 또는 환경 변수. **k3s는 M5부터** |
| **노드 3** | 3대(동일 사양) | **권장(M5~)** — Redis Sentinel · RabbitMQ quorum의 과반 합의에 서로 다른 노드의 투표자 3이 필요하다. 2대면 1개 손실 시 과반을 못 채운다 |

| 노드 | k3s | 레이블 | 고정 IP | vCPU | RAM | 디스크 |
|---|---|---|---|---|---|---|
| node-1 | **server** | `fleet/pg=true` | `192.168.10.11` | 2 | **8GiB** | 40GB |
| node-2 | agent | — | `192.168.10.12` | 2 | **8GiB** | 30GB |
| node-3 | agent | — | `192.168.10.13` | 2 | **8GiB** | 30GB |
| **합** | | | | **6** | **24GB** | **100GB** |

| 결정 | 근거 |
|---|---|
| 전용 control plane을 두지 않는다 | 2GB를 쓰면서 워크로드를 받지 않는 전용 노드는 순수 낭비다. 과반은 어차피 데이터 멤버가 있는 3대로 닫힌다 |
| **CPU가 유일한 제약** | 메모리는 24GB를 써도 호스트에 40GB가 남지만 호스트가 6코어 6스레드(SMT 없음)다. CPU request 합 4.65 core가 6 vCPU 안에 들어가지만 여유가 1.35 core뿐이다 |
| 노드당 **2 vCPU** | 3대 × 2 = 6으로 물리 코어와 1:1. 오버커밋하면 호스트가 굶는다 |
| 메모리 8GiB | 로컬에서는 줄일 이유가 없고, **모니터링을 상시 켤 수 있다** |
| node-1 장애 시 | API 서버가 함께 내려간다 — 기동 중인 pod는 유지, 스케줄링 · 재배치만 정지(08 §10) |

배치와 1대 장애 시 재배치는 08 §10 · §11.

---

## 네임스페이스

```
fleet-app     애플리케이션 (Simulator, FleetController, IngestApi, EventWorker, QueryApi)
fleet-infra   RabbitMQ, Redis, PostgreSQL, MinIO
monitoring    Prometheus, Grafana
ingress-nginx nginx ingress controller
```

애플리케이션과 인프라를 분리하는 이유 — 리소스 쿼터와 네트워크 정책을 따로 걸고,
애플리케이션을 전부 재배포해도 데이터 계층은 건드리지 않기 위함.

---

## 리소스 배분

### 최소 구성 (M3~M5 단계)

| 컴포넌트 | replicas | CPU request | Memory request | Memory limit |
|---|---|---|---|---|
| Simulator | 1 | 500m | 512Mi | 1Gi |
| FleetController | 2 | 200m | 256Mi | 512Mi |
| IngestApi | 2 | 200m | 256Mi | 512Mi |
| EventWorker | 2 | 300m | 256Mi | 512Mi |
| QueryApi | 1 | 100m | 192Mi | 384Mi |
| RabbitMQ | 1 | 300m | 512Mi | 1Gi |
| Redis | 1 | 100m | 256Mi | 512Mi |
| PostgreSQL | 1 | 500m | 512Mi | 1Gi |
| nginx ingress | 1 | 100m | 128Mi | 256Mi |
| **합계** | | **약 2.3 core** | **약 2.9Gi** | |

### 모니터링 추가 (M6)

| 컴포넌트 | CPU | Memory request | 비고 |
|---|---|---|---|
| Prometheus | 300m | 512Mi | 보존 7일 기준. 메모리는 활성 시계열 수에 비례하고, 보존은 디스크가 감당한다 |
| Grafana | 100m | 256Mi | |

### 고가용성 구성 (M5 + M6)

로컬 VM이라 **구성이 하나뿐이다.** 모니터링도 상시 띄운다.

| 컴포넌트 | replicas | CPU req | Mem req | Mem limit | 합계 |
|---|---|---|---|---|---|
| Simulator | 1 (Recreate) | 500m | 512Mi | 1Gi | 512Mi |
| FleetController | 2 | 200m | 256Mi | 512Mi | 512Mi |
| IngestApi | 2 | 200m | 256Mi | 512Mi | 512Mi |
| EventWorker | 2 (HPA **~3**) | 300m | 256Mi | 512Mi | 768Mi |
| QueryApi | 1 | 100m | 192Mi | 384Mi | 192Mi |
| Redis | 3 | 100m | 256Mi | 512Mi | 768Mi |
| Sentinel | 3 | 50m | 32Mi | 64Mi | 96Mi |
| RabbitMQ | 3 | 300m | 512Mi | 1Gi | 1.5Gi |
| PostgreSQL | 1 | 500m | 512Mi | 1Gi | 512Mi |
| nginx ingress | 1 | 100m | 128Mi | 256Mi | 128Mi |
| Prometheus | 1 (보존 7일) | 300m | 512Mi | 1Gi | 512Mi |
| Grafana | 1 | 100m | 256Mi | 512Mi | 256Mi |
| **합계 (EventWorker 3)** | | **4.65 core** | | | **6.16Gi** |

노드 allocatable = 8GiB − 600Mi = **7.4Gi**, 3대 합 **22.2Gi**. 노드별 request와 1대 장애 시 재배치는 08 §11.

| 결정 | 근거 |
|---|---|
| **메모리 8GiB × 3** | 호스트 64GB 중 24GB만 쓰고 40GB가 남는다 |
| **모니터링 상시** | 메모리 여유가 노드당 5Gi씩 있다. 실험 세션에만 켜는 분리가 필요 없다 |
| Prometheus 보존 **7일** · **512Mi** | 보존 기간은 디스크가 감당한다. 메모리는 활성 시계열 수에 비례하므로 512Mi로 충분하다 |
| EventWorker HPA 상한 **3** | **메모리가 아니라 CPU 때문이다.** 상한을 4로 올리면 CPU request가 4.95 core가 되어 1대 장애 시 남은 4 vCPU를 넘는다 |
| limit = request × 2 유지 | .NET GC 특성(아래) |

> **CPU가 유일한 병목이다.** request 합 4.65 core / 6 vCPU, 1대 장애 시 남은 **4 vCPU에 약 4.2 core**가 들어가 여유가 거의 없다.
> EventWorker 증설분이 Pending될 수 있고 HPA 축소로 해소된다. **측정이 걸린 작업(M7)은 AWS에서 한다.**

### 서버 요구사항

| 구성 | 사양 | 가능 범위 |
|---|---|---|
| 단일 노드 | 8GB | 최소 구성 + 모니터링. 데이터 계층 단일 인스턴스 |
| **노드 3** | **노드 3 × 8GiB (2 vCPU)** | **고가용성 구성 + 모니터링** (부하 실험은 M7 AWS) |

> **limit을 request의 2배로 잡는 이유** — .NET은 GC 특성상 순간적으로 메모리를
> 더 쓸 수 있다. limit이 request와 같으면 OOMKilled가 자주 발생한다.
> 반대로 limit이 너무 크면 노드 전체가 메모리 압박을 받는다.

---

## 배포 구성

### 애플리케이션

```yaml
# deploy/k3s/ingest-api.yaml (요약)
apiVersion: apps/v1
kind: Deployment
metadata:
  name: ingest-api
  namespace: fleet-app
spec:
  replicas: 2
  template:
    spec:
      containers:
        - name: ingest-api
          image: registry.local/fleet/ingest-api:0.1.0
          ports:
            - containerPort: 8080
              name: http
          env:
            - name: DOTNET_gcServer
              value: "0"              # 컨테이너에선 워크스테이션 GC가 유리
            - name: RabbitMq__Host
              valueFrom:
                configMapKeyRef: { name: fleet-config, key: rabbitmq-host }
          resources:
            requests: { cpu: 200m, memory: 256Mi }
            limits:   { memory: 512Mi }
          readinessProbe:
            httpGet: { path: /health/ready, port: 8080 }
            initialDelaySeconds: 5
            periodSeconds: 5
          livenessProbe:
            httpGet: { path: /health/live, port: 8080 }
            initialDelaySeconds: 15
            periodSeconds: 20
```

**readiness와 liveness를 구분한다.**

| 프로브 | 검사 내용 | 실패 시 |
|---|---|---|
| `/health/live` | 프로세스가 살아 있는가 | pod 재시작 |
| `/health/ready` | 의존 자원(큐·DB) 연결됐는가 | 트래픽 제외 (재시작 안 함) |

RabbitMQ가 잠깐 끊겼을 때 liveness까지 실패하면 pod가 계속 재시작되며
상황이 악화된다. 의존성 문제는 readiness로만 처리한다.

### Simulator는 Deployment가 아니다

맵 상태를 가지므로 **replicas는 반드시 1**이다.

```yaml
spec:
  replicas: 1
  strategy:
    type: Recreate      # RollingUpdate 금지 — 두 개가 동시에 뜨면 안 됨
```

맵을 여러 개 돌리려면 맵마다 별도 Deployment를 둔다.
**required nodeAffinity `kubernetes.io/hostname NotIn node-1`** — node-1(API 서버) 장애에도 틱이 유지되게 node-2 · 3 중 어디든 둔다(08 §10).

---

## 스토리지

k3s 기본 `local-path`는 노드 로컬 디렉터리를 쓴다. **노드가 바뀌면 데이터에 접근할 수 없다.**

| 데이터 | 전략 |
|---|---|
| PostgreSQL | node-1 고정(`nodeSelector: fleet/pg=true`) + local PV |
| RabbitMQ | 노드마다 1개, local PV `/data/rabbitmq` (08 §10) |
| Redis | 노드마다 1개, local PV `/data/redis`, AOF everysec. 정합성은 Simulator 재주장 (01·08) |
| Sentinel | 노드마다 1개, local PV `/data/sentinel` (설정 파일 재기록 보존) |
| Prometheus | node-3 local-path PVC (보존 7일) |
| MinIO | 용량 큰 디스크 경로 지정 |

```yaml
# PostgreSQL PV 예시
apiVersion: v1
kind: PersistentVolume
metadata:
  name: pg-data
spec:
  capacity: { storage: 20Gi }
  accessModes: [ReadWriteOnce]
  persistentVolumeReclaimPolicy: Retain   # 삭제해도 데이터 보존
  storageClassName: local-storage
  local:
    path: /data/postgres
  nodeAffinity:
    required:
      nodeSelectorTerms:
        - matchExpressions:
            - key: kubernetes.io/hostname
              operator: In
              values: [node-1]
```

> `persistentVolumeReclaimPolicy: Retain`을 반드시 지정한다.
> 기본값 `Delete`면 PVC를 지우는 순간 데이터가 날아간다.

`nodeAffinity`가 노드명(`node-1`)을 가리키므로 **k3s 설치 시 `--node-name` 고정이 전제**다.
로컬 VM이라 PV가 인스턴스와 함께 사라지지 않는다 — 백업은 **VM 스냅샷 + 정기 `pg_dump`**로 한다.

---

## 인그레스

```yaml
apiVersion: networking.k8s.io/v1
kind: Ingress
metadata:
  name: fleet-ingress
  namespace: fleet-app
  annotations:
    nginx.ingress.kubernetes.io/proxy-body-size: "8m"
    nginx.ingress.kubernetes.io/proxy-read-timeout: "3600"   # WebSocket
spec:
  ingressClassName: nginx
  rules:
    - host: fleet.local
      http:
        paths:
          - path: /api/v1/events
            pathType: Prefix
            backend: { service: { name: ingest-api, port: { number: 80 } } }
          - path: /api/v1
            pathType: Prefix
            backend: { service: { name: query-api, port: { number: 80 } } }
          - path: /ws
            pathType: Prefix
            backend: { service: { name: query-api, port: { number: 80 } } }
```

WebSocket 경로는 `proxy-read-timeout`을 길게 잡아야 한다.
기본값(60초)이면 연결이 1분마다 끊긴다.

---

## 모니터링

### 애플리케이션 메트릭

`prometheus-net.AspNetCore`로 `/metrics` 노출.

```csharp
// 커스텀 메트릭
static readonly Counter EventsReceived = Metrics
    .CreateCounter("fleet_events_received_total", "수신 이벤트 수",
        new CounterConfiguration { LabelNames = new[] { "event_type" } });

static readonly Histogram MoveDecisionDuration = Metrics
    .CreateHistogram("fleet_move_decision_seconds", "이동 판단 소요 시간",
        new HistogramConfiguration
        {
            Buckets = Histogram.ExponentialBuckets(0.001, 2, 10)  // 1ms ~ 512ms
        });

static readonly Gauge QueueDepth = Metrics
    .CreateGauge("fleet_queue_depth", "큐 적체량",
        new GaugeConfiguration { LabelNames = new[] { "queue" } });
```

### 수집 대상

| 대상 | 방법 |
|---|---|
| 애플리케이션 | ServiceMonitor 또는 pod annotation |
| RabbitMQ | 내장 prometheus 플러그인 (`rabbitmq_prometheus`) |
| Redis | redis_exporter |
| PostgreSQL | postgres_exporter |
| 노드 | node_exporter |

### 대시보드 구성

| 대시보드 | 패널 |
|---|---|
| **시스템 상태** | pod 상태, CPU/메모리, 재시작 횟수 |
| **파이프라인** | 수신률, 큐 적체, 처리율, 소비 지연 |
| **로봇 운영** | 가동률, 대기 시간 비율, 처리량, 충돌 발생률 |
| **맵** | 혼잡 히트맵, 실시간 로봇 위치 |

### 알림 규칙 (예시)

```yaml
groups:
  - name: fleet
    rules:
      - alert: QueueBacklog
        expr: fleet_queue_depth{queue="fleet.telemetry"} > 10000
        for: 2m
        annotations:
          summary: "텔레메트리 큐 적체 — 워커 처리량 부족"

      - alert: MoveDecisionSlow
        expr: histogram_quantile(0.99, fleet_move_decision_seconds_bucket) > 0.05
        for: 1m
        annotations:
          summary: "이동 판단 p99가 50ms 초과 — 충돌 위험"

      - alert: SimulatorTickOverrun
        expr: rate(fleet_tick_overrun_total[1m]) > 0.1
        for: 1m
        annotations:
          summary: "시뮬레이터가 틱 주기를 못 맞춤 — 로봇 수 한계 도달"
```

---

## 스케일링

### HPA

```yaml
apiVersion: autoscaling/v2
kind: HorizontalPodAutoscaler
metadata:
  name: event-worker
spec:
  scaleTargetRef:
    apiVersion: apps/v1
    kind: Deployment
    name: event-worker
  minReplicas: 2
  maxReplicas: 3          # 파티션 수(16) 이하 · 노드 3대 × 2 vCPU의 CPU 예산
  metrics:
    - type: Resource
      resource:
        name: cpu
        target: { type: Utilization, averageUtilization: 70 }
```

> **maxReplicas는 RabbitMQ 파티션 수(16)를 넘지 않게 설정한다.**
> 파티션이 16개인데 워커를 32개 띄우면 16개는 아무 메시지도 못 받는다.
> 3으로 낮춘 이유는 **메모리가 아니라 CPU**다 — 노드 3대 × 2 vCPU에서 상한 4면 1대 장애 시 남은 4 vCPU를 넘는다("리소스 배분").

워커 수가 바뀔 때의 파티션 재배정(명부 기반 배정 + Single Active Consumer)은 01 "워커 수가 변할 때"를 따른다.
HPA의 `stabilizationWindowSeconds`는 scaleDown 300초로 둔다. 재배정마다 소비자 취소·재구독이 일어나므로
자주 오르내리면 그 자체가 지연이 된다.

큐 적체량 기반 스케일링이 더 정확하지만, 커스텀 메트릭 어댑터가 필요하다.
초기엔 CPU 기준으로 하고, M7 실험 단계에서 전환을 검토한다.

### 스케일 아웃 시연 시나리오

1. 로봇 50대 → 안정 상태 확인
2. 로봇 200대로 증가 → 큐 적체 발생
3. HPA가 워커를 2 → 3개로 증설
4. 적체 해소, 처리율 회복
5. Grafana 그래프로 전 과정 기록

---

## 장애 주입 (M8)

| 시나리오 | 명령 | 확인 사항 |
|---|---|---|
| 워커 pod 강제 종료 | `kubectl delete pod event-worker-xxx` | 메시지 유실 0, 재처리 시 중복 없음 |
| RabbitMQ 재시작 | `kubectl rollout restart sts/rabbitmq` | 재연결, persistent 메시지 보존 |
| Redis 플러시 | `redis-cli FLUSHDB` | `409 state_lost` → Simulator 틱 정지 → 락 재주장 → 재개 (01 "Redis 상태 소실 복구"). 복구 중 로봇 겹침 0 |
| 네트워크 지연 | `tc qdisc add ... delay 200ms` | 이동 판단 지연 시 동작 |
| DB 연결 끊김 | NetworkPolicy로 차단 | readiness 실패, liveness 유지 |

고가용성 시나리오. 모든 행 공통 합격 기준은 **겹침 수 0**. 지표 측정 방법은 08 §12.

| 시나리오 | 명령 | 확인 사항 | 측정 지표 | 합격 기준 |
|---|---|---|---|---|
| Redis primary kill | `SENTINEL get-master-addr-by-name fleet`로 대상 확인 → `kubectl delete pod redis-N --grace-period=0 --force` | Sentinel 승격, 마지막 틱 미복제면 체인 검사 `409` → 재주장 → 재개. 옛 primary pod는 replica로 재합류 | 틱 정지 시간, 장애 조치 시간(`+sdown`~`+switch-master`), `state_lost` 수 · cause, WAIT degraded 수 | 틱 정지 ≤ 30초 |
| Sentinel 1 kill | `kubectl delete pod sentinel-N --force` | 장애 조치 없음, 판정 영향 없음, 재기동 후 Sentinel 3 복귀 | 틱 정지 시간, 판정 p99 | 틱 정지 0, p99 ≤ 10ms |
| RabbitMQ leader 노드 kill | `rabbitmq-queues`로 리더 최다 노드 확인 → `kubectl delete pod rabbitmq-N --force` | quorum 리더 재선출, Ingest 503 → Simulator 순차 재전송, telemetry 파티션 일부 공백 | task · conflict 유실 수, 중복 수, telemetry 유실 수, Ingest 503 수, 리더 재선출 시간 | task · conflict 유실 0, 중복 0, 틱 정지 0 |
| FleetController 1대 kill (배치 처리 중) | `FLEET_FAULT_KILL_AT=after-read · after-commit · before-response` 각 100회 + 무작위 `kubectl delete pod --force` 30분 | 재전송이 다른 인스턴스로 가고, 적용된 판정 = Redis 기록 (08 §4 표) | 틱 정지 시간, 재시도 수, 기록 반환 수, 다이제스트 불일치 수 | 불일치 0, `state_lost` 0, 틱 정지 ≤ 1초 |
| 노드 drain | `kubectl drain node-2 --ignore-daemonsets --delete-emptydir-data` → 10분 후 `uncordon` (대상은 **node-2 또는 node-3** — node-1은 API 서버) | PDB 준수, 그 노드의 Redis · Sentinel · RabbitMQ 멤버 이탈(primary · 리더였으면 장애 조치), 데이터 pod Pending → uncordon 후 재합류 | 틱 정지 시간, task · conflict 유실 수, telemetry 유실 수, 재합류 후 복제 완료 시간 | 틱 정지 ≤ 30초, task · conflict 유실 0 |
| **node-1(API 서버) 장애** | node-1 VM 정지 | 다른 노드의 pod 계속 동작 · **Simulator · FleetController 틱 유지** · 스케줄링 · 재배치 정지 · `kubectl` 불가, 과반 2/3 유지 | 틱 정지 시간, 겹침 수 | 겹침 0, 틱 정지 ≤ 30초(node-1에 Redis primary가 있었을 때만 정지) |
| **노드 강제 소실** | VM 전원 강제 차단(예고 없는 노드 소실). **스냅샷을 떠 두면 같은 상태에서 반복** | 과반 유지 여부, 데이터 pod Pending | 겹침 수, 틱 정지 시간 | 겹침 0 (틱 정지 시간은 기록) |

> 로컬 VM이라 **소실 시점을 통제할 수 있다.** 클라우드의 인스턴스 회수는 시점을 못 고르는 관측 항목이지만,
> 전원 차단은 합격 / 불합격 판정이 가능한 시나리오가 된다.

**각 시나리오마다 Grafana 스크린샷과 결과를 문서로 남긴다.**
포트폴리오에서 "장애를 가정해 설계했다"의 근거가 된다.

---

## 이미지 빌드 · 배포

로컬 레지스트리를 k3s에 등록한다. 로컬 VM이라 레지스트리가 사라지지 않는다(M7 AWS도 같은 이미지를 쓴다).

```yaml
# /etc/rancher/k3s/registries.yaml
mirrors:
  "registry.local:5000":
    endpoint:
      - "http://registry.local:5000"
configs:
  "registry.local:5000":
    tls:
      insecure_skip_verify: true
```

변경 후 `sudo systemctl restart k3s`.

```bash
# 빌드 · 푸시
dotnet publish src/IngestApi -c Release
docker build -t registry.local:5000/fleet/ingest-api:0.1.0 src/IngestApi
docker push registry.local:5000/fleet/ingest-api:0.1.0
```

> 이미지 태그에 `latest`를 쓰지 않는다. `imagePullPolicy`와 엮여
> 어떤 버전이 떠 있는지 알 수 없게 된다. 항상 명시적 버전을 쓴다.
