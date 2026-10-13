using static RobotFleet.Shared.Domain.RobotStatus;

namespace RobotFleet.Shared.Domain;

/// 03 "상태 전이 규칙" 표. 표에 없는 전이는 전부 금지
public static class StatusTransitions
{
    public static IReadOnlyList<(RobotStatus From, RobotStatus To, string Reason)> All { get; } =
    [
        (Idle, Moving, "task_assigned"),
        (Idle, Charging, "charge_at_station"),
        (Idle, Error, "fault"),

        (Moving, Idle, "arrived_park"),
        (Moving, Idle, "arrived_charge_denied"),
        (Moving, Waiting, "conflict_wait"),
        (Moving, Loading, "arrived_origin"),
        (Moving, Unloading, "arrived_destination"),
        (Moving, Charging, "arrived_charger"),
        (Moving, Error, "no_path"),
        (Moving, Error, "replan_exceeded"),
        (Moving, Error, "fault"),

        (Waiting, Moving, "path_cleared"),
        (Waiting, Moving, "replanned"),
        (Waiting, Moving, "yield"),
        (Waiting, Error, "deadlock_unresolved"),
        (Waiting, Error, "replan_exceeded"),   // 막혀 대기 중 재계획 상한 초과(13 §6). M2는 Waiting에서 소진된다
        (Waiting, Error, "fault"),

        (Loading, Moving, "load_done"),
        (Loading, Error, "fault"),

        (Unloading, Idle, "unload_done"),
        (Unloading, Error, "fault"),

        (Charging, Idle, "charge_done"),
        (Charging, Error, "fault"),

        (Error, Idle, "recovered"),
    ];

    public static bool IsAllowed(RobotStatus from, RobotStatus to, string reason) =>
        All.Contains((from, to, reason));
}
