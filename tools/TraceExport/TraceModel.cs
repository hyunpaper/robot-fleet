namespace RobotFleet.TraceExport;

public sealed record Trace(
    string MapId, int Width, int Height, IReadOnlyList<string> Grid, IReadOnlyList<PlaceInfo> Places,
    int TickMs, int MoveMsPerCell, int TurnMsPer90, IReadOnlyList<RobotInfo> Robots, IReadOnlyList<TickFrame> Ticks);

/// <summary>Kind: pickup | dropoff | charger | parking</summary>
public sealed record PlaceInfo(string Id, string Kind, int X, int Y);

public sealed record RobotInfo(string Id, string Start, IReadOnlyList<string> Stops);

public sealed record TickFrame(long Tick, IReadOnlyList<RobotFrame> Robots);

/// <summary>
/// 틱이 끝난 시점의 로봇. H/Th는 Heading 값(N=0, E=1, S=2, W=3).
/// Act가 있으면 진행 중인 동작이고 P는 그 진행률(0~1). Done = 끝낸 정류장 수.
/// </summary>
public sealed record RobotFrame(
    int X, int Y, int H, string Status, string? Act, int? Tx, int? Ty, int? Th, double? P, string? Goal, int Done);
