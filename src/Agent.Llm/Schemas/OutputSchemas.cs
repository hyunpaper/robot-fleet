using System.Text.Json.Nodes;
using RobotFleet.Shared.Tuning;

namespace RobotFleet.Agent.Llm.Schemas;

/// output_config.format 스키마(구조화 출력). 카탈로그에서 만든다 — 키 enum을 손으로 쓰지 않는다. 14 §3.5
/// 모든 객체는 additionalProperties false + 속성 전부 required. minimum · maximum · minItems는 쓰지 않는다(미지원)
public static class OutputSchemas
{
    private static readonly Lazy<string> s_deadlock = new(() => BuildDeadlock().ToJsonString());
    private static readonly Lazy<string> s_infra = new(() => BuildInfra().ToJsonString());

    public static string Deadlock => s_deadlock.Value;

    public static string Infra => s_infra.Value;

    private static JsonObject BuildDeadlock()
    {
        var keys = TuningCatalog.All.Select(p => p.Name);
        var coupled = TuningCatalog.Couplings.Select(c => TuningCatalog.Get(c.Dependent).Name).Distinct();

        // 근거를 결정보다 먼저 둔다 — 생성은 속성 순서를 따른다(14 §3.5)
        return ClosedObject(new JsonObject
        {
            ["rationale"] = Type("string"),
            ["cited_fact_ids"] = StringArrayType(),
            ["decision"] = new JsonObject { ["type"] = "string", ["enum"] = Strings(["propose", "no_change"]) },
            ["change"] = NullOr(Change(keys)),
            ["coupled_change"] = NullOr(Change(coupled)),
        });
    }

    private static JsonObject BuildInfra() => ClosedObject(new JsonObject
    {
        ["explanations"] = new JsonObject
        {
            ["type"] = "array",
            ["items"] = ClosedObject(new JsonObject
            {
                ["candidate_id"] = Type("string"),
                ["explanation"] = Type("string"),
                ["cited_fact_ids"] = StringArrayType(),
            }),
        },
    });

    private static JsonObject Change(IEnumerable<string> keys) => ClosedObject(new JsonObject
    {
        ["parameter_key"] = new JsonObject { ["type"] = "string", ["enum"] = Strings(keys) },
        ["new_value"] = Type("number"),
    });

    /// type object + properties + required(전체, 선언 순) + additionalProperties false
    private static JsonObject ClosedObject(JsonObject properties) => new()
    {
        ["type"] = "object",
        ["properties"] = properties,
        ["required"] = Strings(properties.Select(p => p.Key).ToList()),
        ["additionalProperties"] = false,
    };

    private static JsonObject NullOr(JsonObject schema) => new()
    {
        ["anyOf"] = new JsonArray(Type("null"), schema),
    };

    private static JsonObject StringArrayType() => new() { ["type"] = "array", ["items"] = Type("string") };

    private static JsonObject Type(string type) => new() { ["type"] = type };

    private static JsonArray Strings(IEnumerable<string> values) =>
        new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
}
