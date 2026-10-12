-- 01 PostgreSQL 설계. 날짜 파티션은 003에서 만든다
CREATE TABLE robot_telemetry (
    id          BIGSERIAL,
    map_id      TEXT        NOT NULL,
    robot_id    TEXT        NOT NULL,
    tick        BIGINT      NOT NULL,
    x           SMALLINT    NOT NULL,
    y           SMALLINT    NOT NULL,
    heading     SMALLINT    NOT NULL,
    status      SMALLINT    NOT NULL,
    battery     SMALLINT,
    occurred_at TIMESTAMPTZ NOT NULL
) PARTITION BY RANGE (occurred_at);

CREATE TABLE task_history (
    task_id      TEXT PRIMARY KEY,
    map_id       TEXT NOT NULL,
    robot_id     TEXT,
    task_type    SMALLINT NOT NULL,
    origin_x     SMALLINT, origin_y SMALLINT,
    dest_x       SMALLINT, dest_y SMALLINT,
    created_at   TIMESTAMPTZ NOT NULL,
    assigned_at  TIMESTAMPTZ,
    started_at   TIMESTAMPTZ,
    completed_at TIMESTAMPTZ,
    status       SMALLINT NOT NULL,
    path_length  INT,
    actual_steps INT,
    wait_ms      INT
);

-- 텔레메트리 중복 방지 (파티션 키 포함 필수)
CREATE UNIQUE INDEX ux_robot_telemetry_natural
    ON robot_telemetry (map_id, robot_id, tick, occurred_at);

CREATE TABLE conflict_log (
    id            BIGSERIAL PRIMARY KEY,
    event_id      TEXT NOT NULL UNIQUE,
    map_id        TEXT NOT NULL,
    tick          BIGINT NOT NULL,
    conflict_type SMALLINT NOT NULL,
    x             SMALLINT, y SMALLINT,
    robot_ids     TEXT[] NOT NULL,
    resolution    SMALLINT,
    wait_ms       INT,
    occurred_at   TIMESTAMPTZ NOT NULL
);

CREATE TABLE metrics_minute (
    map_id      TEXT NOT NULL,
    robot_id    TEXT NOT NULL,
    minute      TIMESTAMPTZ NOT NULL,
    move_ms     INT NOT NULL DEFAULT 0,
    work_ms     INT NOT NULL DEFAULT 0,
    wait_ms     INT NOT NULL DEFAULT 0,
    idle_ms     INT NOT NULL DEFAULT 0,
    task_count  INT NOT NULL DEFAULT 0,
    PRIMARY KEY (map_id, robot_id, minute)
);
