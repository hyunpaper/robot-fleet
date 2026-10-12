using System.Text.Json;

namespace RobotFleet.Shared.Map;

public static class MapLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static GridMap LoadFromFile(string path) => LoadFromJson(File.ReadAllText(path));

    public static GridMap LoadFromJson(string json) => GridMap.FromDefinition(ParseDefinition(json));

    /// <summary>
    /// 검증 없이 DTO만. 누락된 필수 속성은 기본값(빈 문자열·0·빈 목록)으로 채워 검증 단계가 잡게 한다.
    /// 목록 안의 null 항목은 DTO로 옮길 수 없으므로 여기서 MapValidationException으로 거부한다.
    /// </summary>
    public static MapDefinition ParseDefinition(string json)
    {
        var raw = JsonSerializer.Deserialize<RawMap>(json, JsonOptions)
                  ?? throw new JsonException("map json is null");

        var nullErrors = new List<string>();
        CollectNullEntries("stations", raw.Stations, nullErrors);
        CollectNullEntries("chargers", raw.Chargers, nullErrors);
        CollectNullEntries("parkings", raw.Parkings, nullErrors);
        if (nullErrors.Count > 0) throw new MapValidationException(nullErrors);

        return new MapDefinition(
            raw.MapId ?? "",
            raw.Version ?? 0,
            raw.Width ?? 0,
            raw.Height ?? 0,
            raw.CellSizeMeters ?? 0,
            raw.Grid ?? new List<string>(),
            (raw.Stations ?? new List<RawStation?>())
                .Select(s => new StationDef(s!.Id ?? "", s.X ?? 0, s.Y ?? 0, s.Type ?? "", s.EquipmentId ?? "")).ToList(),
            (raw.Chargers ?? new List<RawPlace?>()).Select(c => new ChargerDef(c!.Id ?? "", c.X ?? 0, c.Y ?? 0)).ToList(),
            (raw.Parkings ?? new List<RawPlace?>()).Select(p => new ParkingDef(p!.Id ?? "", p.X ?? 0, p.Y ?? 0)).ToList());
    }

    private static void CollectNullEntries<T>(string name, List<T?>? items, List<string> errors) where T : class
    {
        if (items is null) return;
        for (var i = 0; i < items.Count; i++)
            if (items[i] is null) errors.Add($"{name}[{i}] is null");
    }

    // 필드를 전부 nullable로 받는 파싱 전용 DTO
    private sealed record RawMap(
        string? MapId, int? Version, int? Width, int? Height, double? CellSizeMeters,
        List<string>? Grid, List<RawStation?>? Stations, List<RawPlace?>? Chargers, List<RawPlace?>? Parkings);

    private sealed record RawStation(string? Id, int? X, int? Y, string? Type, string? EquipmentId);

    private sealed record RawPlace(string? Id, int? X, int? Y);
}
