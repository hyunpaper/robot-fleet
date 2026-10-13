-- 01 PostgreSQL 설계. 날짜 파티션은 003에서 만든다
-- run 메타 · 실험 조건. run_id는 Simulator가 만들어 03 봉투로 보내고, seq는 DB가 매기는 순번(보고서의 "N회차")
CREATE TABLE simulations (
    run_id        TEXT PRIMARY KEY,
    seq           BIGSERIAL UNIQUE,
    map_code      TEXT NOT NULL,
    environment   TEXT,              -- local | aws (03 SimulationStarted)
    line_code     TEXT,
    pattern_code  TEXT,
    map_version   INT,
    map_hash      TEXT,
    robots        INT,
    speed         REAL,
    tick_ms       INT,
    task_seed     BIGINT,
    params        JSONB,             -- 04 설정값 13종 스냅샷
    started_at    TIMESTAMPTZ,
    ended_at      TIMESTAMPTZ,
    ticks         BIGINT,
    tasks_created INT, tasks_completed INT, tasks_failed INT,
    overruns      BIGINT,
    end_reason    TEXT               -- max_ticks | stopped | faulted
);

-- 로봇 위치 이력 (대용량 · 파티셔닝)
CREATE TABLE robot_telemetry (
    id          BIGSERIAL,
    run_id      TEXT        NOT NULL,
    map_code    TEXT        NOT NULL,
    robot_id    TEXT        NOT NULL,
    tick        BIGINT      NOT NULL,
    x           SMALLINT    NOT NULL,
    y           SMALLINT    NOT NULL,
    heading     SMALLINT    NOT NULL,
    status      SMALLINT    NOT NULL,
    battery     SMALLINT,
    occurred_at TIMESTAMPTZ NOT NULL
) PARTITION BY RANGE (occurred_at);

-- 텔레메트리 중복 방지: run_id 선두, 파티션 키 occurred_at 포함 필수
CREATE UNIQUE INDEX ux_robot_telemetry_natural
    ON robot_telemetry (run_id, robot_id, tick, occurred_at);

-- 작업 이력
CREATE TABLE task_history (
    run_id       TEXT NOT NULL,
    task_id      TEXT NOT NULL,
    map_code     TEXT NOT NULL,
    robot_id     TEXT,
    task_type    SMALLINT NOT NULL,
    origin_x     SMALLINT, origin_y SMALLINT,
    dest_x       SMALLINT, dest_y SMALLINT,
    origin_station_code TEXT, dest_station_code TEXT,   -- 03 TransportTask 설비 식별자
    origin_equip_code   TEXT, dest_equip_code   TEXT,
    created_at   TIMESTAMPTZ NOT NULL,
    assigned_at  TIMESTAMPTZ,
    started_at   TIMESTAMPTZ,
    completed_at TIMESTAMPTZ,
    status       SMALLINT NOT NULL,
    path_length  INT,          -- 계획 경로 길이
    actual_steps INT,          -- 실제 이동 칸 수
    wait_ms      INT,          -- 충돌 대기 누적
    PRIMARY KEY (run_id, task_id)
);

-- 충돌 · 교착 이력. PK · UNIQUE는 그대로(event_id = {batchId}/{index}, batchId가 UUIDv7이라 run 간 충돌 없음)
CREATE TABLE conflict_log (
    id                BIGSERIAL PRIMARY KEY,
    event_id          TEXT NOT NULL UNIQUE,   -- "{batchId}/{index}" — 워커 2차 멱등
    run_id            TEXT NOT NULL,
    map_code          TEXT NOT NULL,
    tick              BIGINT NOT NULL,
    conflict_type     SMALLINT NOT NULL,   -- vertex / edge / following / deadlock
    x                 SMALLINT, y SMALLINT,
    robot_ids         TEXT[] NOT NULL,
    resolution        SMALLINT,            -- wait / reroute / yield / failed
    wait_ms           INT,                 -- 충돌 대기(ConflictDetected)
    resolved_ms       INT,                 -- 교착 해소 시간(DeadlockDetected) — 05 "교착 해소 시간"
    yield_robot_id    TEXT,
    yield_to_x        SMALLINT, yield_to_y SMALLINT,
    searched_distance SMALLINT,
    occurred_at       TIMESTAMPTZ NOT NULL
);
CREATE INDEX ix_conflict_log_run ON conflict_log (run_id);

-- 재계획 이력 (05 재계획률 · 실험 5 · M9 replan_storm)
CREATE TABLE replan_log (
    event_id    TEXT PRIMARY KEY,
    run_id      TEXT NOT NULL,
    map_code    TEXT NOT NULL,
    robot_id    TEXT NOT NULL,
    task_id     TEXT,
    tick        BIGINT NOT NULL,
    reason      SMALLINT NOT NULL,   -- blocked | deadlock | map_changed
    old_length  INT, new_length INT,
    compute_ms  REAL,
    occurred_at TIMESTAMPTZ NOT NULL
);
CREATE INDEX ix_replan_log_run ON replan_log (run_id);

-- 집계 (분 단위 롤업)
CREATE TABLE metrics_minute (
    run_id      TEXT NOT NULL,
    map_code    TEXT NOT NULL,
    robot_id    TEXT NOT NULL,
    minute      TIMESTAMPTZ NOT NULL,
    move_ms     INT NOT NULL DEFAULT 0,
    work_ms     INT NOT NULL DEFAULT 0,
    wait_ms     INT NOT NULL DEFAULT 0,
    idle_ms     INT NOT NULL DEFAULT 0,
    task_count  INT NOT NULL DEFAULT 0,
    PRIMARY KEY (run_id, robot_id, minute)
);

-- run 단위 결과 집계. simulations는 조건(입력), run_metrics는 결과 — 지표 정의가 바뀌어도 재집계만 한다
CREATE TABLE run_metrics (
    run_id        TEXT PRIMARY KEY,
    -- 시간 분해 (전체 로봇 합)
    move_ms BIGINT, work_ms BIGINT, wait_ms BIGINT, idle_ms BIGINT,
    utilization   REAL,   -- (move+work)/전체
    wait_ratio    REAL,   -- 혼잡 손실률
    idle_ratio    REAL,
    -- 대기
    wait_count    INT,    -- conflict_log 건수(대기 구간당 ConflictDetected 1건)
    wait_ms_avg   REAL,   -- wait_ms / wait_count
    -- 작업
    throughput_per_hour REAL,
    lead_time_p50 INT, lead_time_p90 INT, lead_time_p99 INT,
    path_efficiency REAL, -- SUM(path_length)/SUM(actual_steps)
    task_failed_ratio REAL,
    -- 알고리즘 품질
    conflict_count INT, deadlock_count INT, deadlock_resolve_ms_avg REAL,
    replan_count INT, replan_per_task REAL,
    -- 시스템
    tick_overruns BIGINT, stall_ms BIGINT,
    computed_at   TIMESTAMPTZ
);

-- 기준정보 (01 기준정보). 사실 테이블에서는 FK를 걸지 않는다
CREATE TABLE lines (
    line_id     BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    line_code   TEXT NOT NULL,                    -- "L-A"
    name        TEXT NOT NULL,
    description TEXT,
    active      BOOLEAN NOT NULL DEFAULT true,
    CONSTRAINT uq_lines_code UNIQUE (line_code),
    CONSTRAINT uq_lines_name UNIQUE (name)
);

CREATE TABLE equipment_types (
    equip_type_id   BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    equip_type_code TEXT NOT NULL,                -- "press" | "inspect" | "buffer"
    name            TEXT NOT NULL,
    default_work_ms INT  NOT NULL DEFAULT 2000,
    CONSTRAINT uq_equip_types_code UNIQUE (equip_type_code),
    CONSTRAINT uq_equip_types_name UNIQUE (name),
    CONSTRAINT ck_equip_types_work CHECK (default_work_ms >= 0)
);

CREATE TABLE equipment (
    equip_id      BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    equip_code    TEXT   NOT NULL,                -- "EQ-01" — 맵 파일이 쓰는 값
    line_id       BIGINT NOT NULL REFERENCES lines(line_id),
    equip_type_id BIGINT NOT NULL REFERENCES equipment_types(equip_type_id),
    name          TEXT   NOT NULL,
    work_ms       INT    NOT NULL DEFAULT 2000,
    capacity      INT    NOT NULL DEFAULT 1,
    active        BOOLEAN NOT NULL DEFAULT true,
    CONSTRAINT uq_equipment_code     UNIQUE (equip_code),
    CONSTRAINT uq_equipment_name     UNIQUE (line_id, name),   -- 라인 안에서 설비명 유일
    CONSTRAINT ck_equipment_capacity CHECK (capacity >= 1),
    CONSTRAINT ck_equipment_work     CHECK (work_ms  >= 0)
);

CREATE TABLE maps (
    map_id      BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    map_code    TEXT   NOT NULL,                  -- "factory-a" — 이벤트의 mapId
    version     INT    NOT NULL,
    line_id     BIGINT NOT NULL REFERENCES lines(line_id),
    name        TEXT   NOT NULL,
    width       SMALLINT NOT NULL, height SMALLINT NOT NULL,
    cell_size_m REAL   NOT NULL DEFAULT 1.0,
    grid        JSONB  NOT NULL,
    map_hash    TEXT   NOT NULL,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT uq_maps_code_version UNIQUE (map_code, version),   -- 같은 맵의 같은 버전 금지
    CONSTRAINT uq_maps_code_hash    UNIQUE (map_code, map_hash),  -- 같은 내용을 두 버전으로 금지
    CONSTRAINT uq_maps_line_name_ver UNIQUE (line_id, name, version),
    CONSTRAINT ck_maps_size    CHECK (width > 0 AND height > 0 AND cell_size_m > 0),
    CONSTRAINT ck_maps_version CHECK (version >= 1)
);

CREATE TABLE stations (
    station_id   BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    station_code TEXT   NOT NULL,                 -- "ST-01" | "CH-01" | "PK-01"
    map_id       BIGINT NOT NULL REFERENCES maps(map_id) ON DELETE CASCADE,
    kind         SMALLINT NOT NULL,               -- 2=Station 3=Charge 4=Parking (03)
    equip_id     BIGINT REFERENCES equipment(equip_id),   -- Charge · Parking 은 NULL
    io_type      TEXT,                            -- pickup | dropoff
    x SMALLINT NOT NULL, y SMALLINT NOT NULL,
    CONSTRAINT uq_stations_code UNIQUE (map_id, station_code),
    CONSTRAINT uq_stations_cell UNIQUE (map_id, x, y),   -- 한 칸에 접점 둘 금지
    CONSTRAINT ck_stations_kind CHECK (kind IN (2, 3, 4)),
    CONSTRAINT ck_stations_xy   CHECK (x >= 0 AND y >= 0),
    CONSTRAINT ck_stations_equipment CHECK (
        (kind = 2 AND equip_id IS NOT NULL AND io_type IN ('pickup','dropoff'))
     OR (kind IN (3,4) AND equip_id IS NULL AND io_type IS NULL))
);

CREATE TABLE task_patterns (
    pattern_id   BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    pattern_code TEXT   NOT NULL,                 -- "P-A-standard"
    line_id      BIGINT NOT NULL REFERENCES lines(line_id),
    name         TEXT   NOT NULL,
    active       BOOLEAN NOT NULL DEFAULT true,
    CONSTRAINT uq_patterns_code UNIQUE (pattern_code),
    CONSTRAINT uq_patterns_name UNIQUE (line_id, name)
);

CREATE TABLE task_pattern_flows (
    flow_id        BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    pattern_id     BIGINT NOT NULL REFERENCES task_patterns(pattern_id) ON DELETE CASCADE,
    seq            INT    NOT NULL,
    from_equip_id  BIGINT NOT NULL REFERENCES equipment(equip_id),
    to_equip_id    BIGINT NOT NULL REFERENCES equipment(equip_id),
    weight         REAL     NOT NULL DEFAULT 1,
    priority       SMALLINT NOT NULL DEFAULT 0,   -- 0=normal 1=high (03)
    CONSTRAINT uq_flow_seq      UNIQUE (pattern_id, seq),
    CONSTRAINT uq_flow          UNIQUE (pattern_id, from_equip_id, to_equip_id, priority),
    CONSTRAINT ck_flow_distinct CHECK (from_equip_id <> to_equip_id),
    CONSTRAINT ck_flow_weight   CHECK (weight > 0),
    CONSTRAINT ck_flow_priority CHECK (priority IN (0, 1))
);
