# 02. 인프라

## 환경

- 자체 리눅스 서버 (클라우드 미사용)
- 쿠버네티스 배포판: **k3s**
- 인그레스: nginx ingress controller (k3s 기본 Traefik 비활성화)

---

## k3s 설치

```bash
curl -sfL https://get.k3s.io | sh -s - \
  --disable traefik \
  --disable servicelb \
  --write-kubeconfig-mode 644
```

| 옵션 | 이유 |
|---|---|
| `--disable traefik` | nginx ingress를 직접 설치 — 현업 환경과 맞춤 |
| `--disable servicelb` | MetalLB 또는 NodePort 사용 |
| `--write-kubeconfig-mode 644` | sudo 없이 kubectl 사용 |

### 노드 구성

| 구성 | 조건 | 비고 |
|---|---|---|
| 단일 노드 | 서버 1대 | 시작 가능(M3~M4). pod 스케일 아웃 시연은 됨 |
| **cp-1 + worker 3** | 4대 (VM 가능) | **권장(M5~)** — Redis Sentinel·RabbitMQ quorum의 과반 합의에 worker 3대가 필요하다. control plane은 워크로드와 분리 (07) |

| 노드 | 주소(예시) | 역할 |
|---|---|---|
| cp-1 | 192.168.10.10 | k3s server 전용. taint로 워크로드 배제 |
| worker-1 | 192.168.10.11 | 애플리케이션 · PostgreSQL · Redis/Sentinel/RabbitMQ 멤버 |
| worker-2 | 192.168.10.12 | 애플리케이션 · Redis/Sentinel/RabbitMQ 멤버 |
| worker-3 | 192.168.10.13 | 애플리케이션 · 모니터링 · Redis/Sentinel/RabbitMQ 멤버 |

```bash
# cp-1 (server) — 워크로드를 받지 않는다
curl -sfL https://get.k3s.io | sh -s - --disable traefik --disable servicelb \
  --node-taint node-role.kubernetes.io/control-plane=true:NoSchedule

# worker-1~3 (agent) — cp-1에서 토큰 확인
sudo cat /var/lib/rancher/k3s/server/node-token

curl -sfL https://get.k3s.io | K3S_URL=https://192.168.10.10:6443 \
  K3S_TOKEN=<token> sh -s - --kubelet-arg=system-reserved=memory=600Mi

# PostgreSQL 고정 레이블
kubectl label node worker-1 fleet/pg=true
```

노드 사양과 배치 근거는 08 §10.

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
| Prometheus | 300m | 1Gi | 보존 기간 7일 기준 |
| Grafana | 100m | 256Mi | |

### 고가용성 구성 (M5 + M6)

worker 1대 4 vCPU · 6GiB × 3. cp-1(2 vCPU · 2GB)은 워크로드가 없어 표에서 뺀다. worker별 배치와 1대 장애 시 재배치 계산은 08 §11.

| 컴포넌트 | replicas | CPU request | Memory request | Memory limit | 합계 request |
|---|---|---|---|---|---|
| Simulator | 1 | 500m | 512Mi | 1Gi | 512Mi |
| FleetController | 2 | 200m | 256Mi | 512Mi | 512Mi |
| IngestApi | 2 | 200m | 256Mi | 512Mi | 512Mi |
| EventWorker | 2 (HPA ~8) | 300m | 256Mi | 512Mi | 512Mi (최대 2Gi) |
| QueryApi | 1 | 100m | 192Mi | 384Mi | 192Mi |
| Redis | 3 | 100m | 256Mi | 512Mi | 768Mi |
| Sentinel | 3 | 50m | 32Mi | 64Mi | 96Mi |
| RabbitMQ | 3 | 300m | 512Mi | 1Gi | 1.5Gi |
| PostgreSQL | 1 | 500m | 512Mi | 1Gi | 512Mi |
| nginx ingress | 1 | 100m | 128Mi | 256Mi | 128Mi |
| Prometheus | 1 | 300m | 1Gi | 2Gi | 1Gi |
| Grafana | 1 | 100m | 256Mi | 512Mi | 256Mi |
| **합계** | | **4.35 core (최대 6.15)** | | | **6.4Gi (EventWorker 8대 시 7.9Gi)** |

> request 합 8Gi 이하, limit = request × 2. worker마다 시스템 예약 600Mi를 빼면 allocatable 5.4Gi. 정상 시 worker별 request 최대 2.8Gi, worker 1대 장애 후에도 남은 2대에 애플리케이션 pod가 모두 재배치된다(최대 4.3Gi). Pending은 노드에 묶인 데이터 멤버 · PostgreSQL · Prometheus뿐 (08 §11).

### 서버 요구사항

| 구성 | 메모리 | 가능 범위 |
|---|---|---|
| 단일 노드 | 8GB | 최소 구성 + 모니터링. 데이터 계층 단일 인스턴스 |
| **cp-1 + worker 3** | cp-1 2GB + **worker 6GiB × 3** | **고가용성 구성 + 모니터링 + 부하 시험** |

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

---

## 스토리지

k3s 기본 `local-path`는 노드 로컬 디렉터리를 쓴다. **노드가 바뀌면 데이터에 접근할 수 없다.**

| 데이터 | 전략 |
|---|---|
| PostgreSQL | worker-1 고정(`nodeSelector: fleet/pg=true`) + local PV |
| RabbitMQ | worker마다 1개, local PV `/data/rabbitmq` (08 §10) |
| Redis | worker마다 1개, local PV `/data/redis`, AOF everysec. 정합성은 Simulator 재주장 (01·08) |
| Sentinel | worker마다 1개, local PV `/data/sentinel` (설정 파일 재기록 보존) |
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
              values: [worker-1]
```

> `persistentVolumeReclaimPolicy: Retain`을 반드시 지정한다.
> 기본값 `Delete`면 PVC를 지우는 순간 데이터가 날아간다.

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
  maxReplicas: 8          # 파티션 수를 넘지 않게
  metrics:
    - type: Resource
      resource:
        name: cpu
        target: { type: Utilization, averageUtilization: 70 }
```

> **maxReplicas는 RabbitMQ 파티션 수(16)를 넘지 않게 설정한다.**
> 파티션이 16개인데 워커를 32개 띄우면 16개는 아무 메시지도 못 받는다.

워커 수가 바뀔 때의 파티션 재배정(명부 기반 배정 + Single Active Consumer)은 01 "워커 수가 변할 때"를 따른다.
HPA의 `stabilizationWindowSeconds`는 scaleDown 300초로 둔다. 재배정마다 소비자 취소·재구독이 일어나므로
자주 오르내리면 그 자체가 지연이 된다.

큐 적체량 기반 스케일링이 더 정확하지만, 커스텀 메트릭 어댑터가 필요하다.
초기엔 CPU 기준으로 하고, M7 실험 단계에서 전환을 검토한다.

### 스케일 아웃 시연 시나리오

1. 로봇 50대 → 안정 상태 확인
2. 로봇 200대로 증가 → 큐 적체 발생
3. HPA가 워커를 2 → 6개로 증설
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
| worker drain | `kubectl drain worker-2 --ignore-daemonsets --delete-emptydir-data` → 10분 후 `uncordon` | PDB 준수, 그 worker의 Redis · Sentinel · RabbitMQ 멤버 이탈(primary · 리더였으면 장애 조치), 데이터 pod Pending → uncordon 후 재합류 | 틱 정지 시간, task · conflict 유실 수, telemetry 유실 수, 재합류 후 복제 완료 시간 | 틱 정지 ≤ 30초, task · conflict 유실 0 |

**각 시나리오마다 Grafana 스크린샷과 결과를 문서로 남긴다.**
포트폴리오에서 "장애를 가정해 설계했다"의 근거가 된다.

---

## 이미지 빌드 · 배포

로컬 레지스트리를 k3s에 등록한다.

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
