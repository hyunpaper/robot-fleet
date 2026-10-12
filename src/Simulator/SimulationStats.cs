namespace RobotFleet.Simulator;

public sealed class SimulationStats
{
    public long Tick { get; set; }
    public int OverrunCount { get; set; }
    public long TotalMoveMs, TotalTurnMs;      // 05 시간 분해의 씨앗. M1은 Move만 의미 있음
    public TimeSpan WallClock { get; set; }
}
