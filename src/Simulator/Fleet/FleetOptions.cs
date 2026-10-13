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

    public long MaxTicks { get; init; } = 100_000;

    /// 배치 봉투에 싣는 run 식별자(03). 생성은 호출부 몫
    public string RunId { get; init; } = "";
}

/// ExitCode: 0 MaxTicks 도달, 4 송신 버퍼 닫힘(400 등), 130 취소
public sealed record FleetRunResult(long Ticks, int TasksCreated, int TasksCompleted, int TasksFailed,
    long Overruns, TimeSpan StallTime, int ExitCode);
