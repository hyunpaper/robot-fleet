namespace RobotFleet.Shared.Map;

/// <summary>maps/*.json과 1:1 대응하는 DTO. 검증 전 상태.</summary>
public sealed record MapDefinition(
    string MapId,
    int Version,
    int Width,
    int Height,
    double CellSizeMeters,
    IReadOnlyList<string> Grid,
    IReadOnlyList<StationDef> Stations,
    IReadOnlyList<ChargerDef> Chargers,
    IReadOnlyList<ParkingDef> Parkings);

public sealed record StationDef(string Id, int X, int Y, string Type, string EquipmentId);  // Type: "pickup" | "dropoff"

public sealed record ChargerDef(string Id, int X, int Y);

public sealed record ParkingDef(string Id, int X, int Y);
