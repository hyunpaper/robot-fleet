namespace RobotFleet.FleetController.Arbitration;

/// <summary>04 "우선순위 정책"의 기본(대기 시간 가중 복합) 점수. 높을수록 먼저 판정.</summary>
public static class MovePriority
{
    public static double Score(in MoveRequest r)
        => r.WaitMs * 0.01
         + (100 - r.RemainingSteps)
         + r.TaskPriority * 50;
}
