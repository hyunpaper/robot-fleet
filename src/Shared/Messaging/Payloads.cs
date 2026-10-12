namespace RobotFleet.Shared.Messaging;

public sealed record CellDto(int X, int Y);

public sealed record RobotTelemetryPayload(string RobotId, int X, int Y, int Heading, int Status, int Battery,
    string? TaskId, int PathIndex, int PathLength);

public sealed record TaskCreatedPayload(string TaskId, int Type, CellDto Origin, CellDto Destination, int Priority);

public sealed record TaskAssignedPayload(string TaskId, string RobotId, int PlannedPathLength, int EstimatedDurationMs);

public sealed record TaskStartedPayload(string TaskId, string RobotId);

public sealed record TaskCompletedPayload(string TaskId, string RobotId, int ActualSteps, int MoveMs, int WaitMs,
    int WorkMs, int ReplanCount);

public sealed record TaskFailedPayload(string TaskId, string? RobotId, string Reason, int AtX, int AtY);

public sealed record ConflictDetectedPayload(string ConflictType, int X, int Y, IReadOnlyList<string> RobotIds,
    string WinnerRobotId, string Resolution, int WaitMs);

public sealed record DeadlockDetectedPayload(IReadOnlyList<string> Cycle, IReadOnlyList<CellDto> Cells,
    string Resolution, string YieldRobotId, int YieldToX, int YieldToY, int ResolvedMs);

public sealed record PathReplannedPayload(string RobotId, string? TaskId, string Reason, int OldLength,
    int NewLength, double ComputeMs);

public sealed record RobotStatusChangedPayload(string RobotId, int From, int To, string Reason);
