-- 10 §8 파티션. 날짜 경계는 UTC
CREATE TABLE IF NOT EXISTS robot_telemetry_default PARTITION OF robot_telemetry DEFAULT;

CREATE OR REPLACE FUNCTION fleet_ensure_telemetry_partition(day date) RETURNS text
LANGUAGE plpgsql AS $$
DECLARE name text := format('robot_telemetry_%s', to_char(day, 'YYYYMMDD'));
BEGIN
    EXECUTE format('CREATE TABLE IF NOT EXISTS %I PARTITION OF robot_telemetry FOR VALUES FROM (%L) TO (%L)',
                   name, day::timestamp AT TIME ZONE 'UTC', (day + 1)::timestamp AT TIME ZONE 'UTC');
    RETURN name;
END $$;
