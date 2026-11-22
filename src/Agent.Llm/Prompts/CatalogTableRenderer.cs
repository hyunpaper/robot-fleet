namespace RobotFleet.Agent.Llm.Prompts;

/// 교착 시스템 프롬프트의 {{CATALOG}} — TuningCatalog를 마크다운 표 + 연동 제약 줄로 그린다.
/// InvariantCulture, 현재 값은 넣지 않는다(캐시 접두 고정). 14 §3.4
public static class CatalogTableRenderer
{
    public static string Render() => throw new NotImplementedException();
}
