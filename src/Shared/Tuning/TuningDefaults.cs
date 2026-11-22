namespace RobotFleet.Shared.Tuning;

/// 04 "## 설정값" 기본값의 단일 정의. 옵션 객체 기본값과 TuningCatalog.Default가 이 상수를 참조한다. 14 §2.1
/// 컴파일 시간 상수라 PathCost(double TurnPer90 = ...) 같은 선택 매개변수 기본값에도 쓸 수 있다
public static class TuningDefaults
{
    public const double CongestionAlpha = 0;            // 1
    public const double TurnCost = 0.5;                 // 2
    public const double PriorityWaitWeight = 0.01;      // 3
    public const double PriorityDistanceWeight = 1.0;   // 4
    public const double PriorityTaskPriorityWeight = 50; // 5
    public const int ReplanWaitThresholdMs = 500;       // 6
    public const int ReplanCooldownMs = 100;            // 7
    public const int ReplanMaxPerTask = 5;              // 8
    public const int DeadlockDetectWaitMs = 1000;       // 9
    public const int YieldSearchMaxDistance = 5;        // 10
    public const double YieldCostDistanceWeight = 1.0;  // 11
    public const double YieldCostPriorityWeight = 1.0;  // 12
    public const int DeadlockResolveTtlTicks = 50;      // 13
}
