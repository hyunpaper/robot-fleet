namespace RobotFleet.TraceExport;

/// <summary>Ticks[LoopStart]부터 LoopTicks개가 한 주기. Ticks[LoopStart + LoopTicks]는 Ticks[LoopStart]와 같다.</summary>
public sealed record Trace(
    string MapId, int Width, int Height, IReadOnlyList<string> Grid, IReadOnlyList<PlaceInfo> Places,
    int TickMs, int MoveMsPerCell, int TurnMsPer90, IReadOnlyList<RobotInfo> Robots,
    int LoopStart, int LoopTicks, IReadOnlyList<TickFrame> Ticks);

/// <summary>Kind: pickup | dropoff | charger | parking</summary>
public sealed record PlaceInfo(string Id, string Kind, int X, int Y, string? EquipmentId);

public sealed record RobotInfo(string Id, IReadOnlyList<string> Loop);

public sealed record TickFrame(long Tick, IReadOnlyList<RobotFrame> Robots);

/// <summary>
/// 틱이 끝난 시점의 로봇. H/Th는 Heading 값(N=0, E=1, S=2, W=3).
/// Act가 있으면 진행 중인 동작이고 P는 그 진행률(0~1). Goal = 향하는 장소, At = 작업 중인 장소, Wp = 작업 진행률, Wx/Wy = 대기 중일 때 들어가려던 칸.
/// </summary>
public sealed record RobotFrame(
    int X, int Y, int H, string Status, string? Act, int? Tx, int? Ty, int? Th, double? P,
    string? Goal, string? At, double? Wp, int? Wx, int? Wy);
