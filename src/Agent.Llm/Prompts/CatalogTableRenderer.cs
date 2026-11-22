using System.Globalization;
using RobotFleet.Shared.Tuning;

namespace RobotFleet.Agent.Llm.Prompts;

/// 교착 시스템 프롬프트의 {{CATALOG}} — TuningCatalog를 마크다운 표 + 연동 제약 줄로 그린다.
/// InvariantCulture, 현재 값은 넣지 않는다(캐시 접두 고정). 14 §3.4
public static class CatalogTableRenderer
{
    public static string Render()
    {
        var lines = new List<string>
        {
            "| 키 | 형식 | 기본값 | 허용 범위 | 1회 변경 상한 | 다루는 동작 | 영향 지표 |",
            "|---|---|---|---|---|---|---|",
        };

        foreach (var p in TuningCatalog.All)
        {
            var kind = p.Kind == TuningValueKind.Integer ? "정수" : "실수";
            lines.Add($"| {p.Name} | {kind} | {Num(p.Default)} | {Num(p.Min)} ~ {Num(p.Max)} | ±{Num(p.MaxStep)} | {p.Topic} | {p.Metrics} |");
        }

        lines.Add(string.Empty);
        foreach (var c in TuningCatalog.Couplings)
        {
            lines.Add($"연동 제약: {TuningCatalog.Get(c.Dependent).Name} >= {TuningCatalog.Get(c.Driver).Name} × {Num(c.Factor)}");
        }

        return string.Join("\n", lines);
    }

    private static string Num(double v) => v.ToString(CultureInfo.InvariantCulture);
}
