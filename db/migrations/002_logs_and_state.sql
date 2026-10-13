-- 10 §8
CREATE TABLE task_event_log (
    event_id    TEXT PRIMARY KEY,
    run_id      TEXT NOT NULL,
    map_code    TEXT NOT NULL,
    task_id     TEXT NOT NULL,
    event_type  TEXT NOT NULL,
    tick        BIGINT NOT NULL,
    occurred_at TIMESTAMPTZ NOT NULL
);
CREATE INDEX ix_task_event_log_run ON task_event_log (run_id);
CREATE INDEX ix_task_event_log_task ON task_event_log (run_id, task_id);

CREATE TABLE robot_status_log (
    event_id    TEXT PRIMARY KEY,
    run_id      TEXT NOT NULL,
    map_code    TEXT NOT NULL,
    robot_id    TEXT NOT NULL,
    tick        BIGINT NOT NULL,
    from_status SMALLINT NOT NULL,
    to_status   SMALLINT NOT NULL,
    reason      TEXT NOT NULL,
    valid       BOOLEAN NOT NULL,          -- 전이 검증 결과 (10 §6)
    occurred_at TIMESTAMPTZ NOT NULL
);
CREATE INDEX ix_robot_status_log_run ON robot_status_log (run_id);
CREATE INDEX ix_robot_status_log_invalid ON robot_status_log (run_id) WHERE NOT valid;

CREATE TABLE robot_state (
    run_id        TEXT NOT NULL,
    robot_id      TEXT NOT NULL,
    map_code      TEXT NOT NULL,
    status        SMALLINT NOT NULL,
    last_tick     BIGINT NOT NULL,
    last_event_id TEXT NOT NULL,
    PRIMARY KEY (run_id, robot_id)
);
