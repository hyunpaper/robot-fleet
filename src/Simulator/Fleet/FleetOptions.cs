namespace RobotFleet.Simulator.Fleet;

/// 다대 로봇 시뮬레이션 설정. 10 §1 · §9
public sealed record FleetOptions
{
    public int Robots { get; init; } = 20;

    /// 작업 발생률(건/초, 시뮬레이션 시간)
    public double Lambda { get; init; } = 0.5;

    public int Seed { get; init; } = 1;

    /// 적재 · 하역 체류(ms)
    public int WorkMs { get; init; } = 2000;

    public int TickMs { get; init; } = 100;

    /// 실시간 대기 = TickMs / SpeedFactor. 0이면 대기 없음
    public double SpeedFactor { get; init; } = 1.0;

    public int MoveMsPerCell { get; init; } = 1000;

    public int TurnMsPer90 { get; init; } = 300;

    /// 재계획 트리거: 대기 임계(ms). 04 설정값 6
    public int ReplanWaitThresholdMs { get; init; } = 500;

    /// 같은 로봇의 재계획 쿨다운(ms). 04 설정값 7
    public int ReplanCooldownMs { get; init; } = 100;

    /// 같은 작업의 재계획 상한(초과 시 작업 실패). 04 설정값 8
    public int ReplanMaxPerTask { get; init; } = 5;

    public long MaxTicks { get; init; } = 100_000;

    /// 배치 봉투에 싣는 run 식별자(03). 비어 있으면 FleetSimulation이 RunIds.New로 만든다
    public string RunId { get; init; } = "";

    /// SimulationStarted.environment — local | aws (03)
    public string Environment { get; init; } = "local";

    /// 기준정보 코드(01). 아직 적재 전이면 빈 값 → DB NULL
    public string LineCode { get; init; } = "";

    public string TaskPatternCode { get; init; } = "";

    /// 맵 파일 해시(RunIds.MapHash). 빈 값이면 DB 대조를 하지 않는다
    public string MapHash { get; init; } = "";
}

/// ExitCode: 0 MaxTicks 도달, 4 송신 버퍼 닫힘(400 등), 130 취소
public sealed record FleetRunResult(long Ticks, int TasksCreated, int TasksCompleted, int TasksFailed,
    long Overruns, TimeSpan StallTime, int ExitCode);
