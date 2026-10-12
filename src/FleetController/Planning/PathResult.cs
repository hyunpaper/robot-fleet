using RobotFleet.Shared.Geometry;

namespace RobotFleet.FleetController.Planning;

public enum PathStatus : byte
{
    Found = 0,
    AlreadyAtGoal = 1,   // start == goal. Steps = [ (start, startHeading) ], Cost = 0
    NoPath = 2,          // 둘 다 유효하지만 연결되지 않음
    InvalidStart = 3,    // 범위 밖 또는 Blocked — 로봇 상태가 깨진 것. 호출자가 Error 처리
    InvalidGoal = 4,     // 범위 밖 또는 Blocked — 작업 정의가 깨진 것. 호출자가 Failed 처리
}

public sealed record PathResult
{
    public required PathStatus Status { get; init; }
    /// <summary>Steps[0] = (start, startHeading). 마지막 = goal. Found/AlreadyAtGoal 외에는 빈 배열.</summary>
    public required IReadOnlyList<PathStep> Steps { get; init; }
    public required double Cost { get; init; }
    public required int ExpandedNodes { get; init; }     // 성능 테스트·메트릭용
    public TimeSpan Elapsed { get; init; }

    public bool IsSuccess => Status is PathStatus.Found or PathStatus.AlreadyAtGoal;
    /// <summary>이동 칸 수 = Steps.Count - 1. 03의 plannedPathLength가 이 값.</summary>
    public int MoveCount => Math.Max(0, Steps.Count - 1);

    public static PathResult Failure(PathStatus status, int expanded, TimeSpan elapsed) => new()
    {
        Status = status,
        Steps = Array.Empty<PathStep>(),
        Cost = 0,
        ExpandedNodes = expanded,
        Elapsed = elapsed,
    };
}
