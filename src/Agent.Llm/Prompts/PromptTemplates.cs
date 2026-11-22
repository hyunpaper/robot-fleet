using System.Reflection;

namespace RobotFleet.Agent.Llm.Prompts;

/// 시스템 프롬프트 원문(임베디드 리소스 Prompts/*.txt). 원문 · 스키마가 바뀌면 버전을 올린다. 14 §3.3
/// 줄바꿈은 \n으로 고정한다 — 캐시 접두가 바이트 단위라서(14 §1)
public static class PromptTemplates
{
    public const string DeadlockVersion = "deadlock-v1";
    public const string InfraVersion = "infra-v1";

    private const string CatalogPlaceholder = "{{CATALOG}}";

    private static readonly Lazy<string> s_deadlock = new(() =>
        Load("deadlock-system.txt").Replace(CatalogPlaceholder, CatalogTableRenderer.Render(), StringComparison.Ordinal));

    private static readonly Lazy<string> s_infra = new(() => Load("infra-system.txt"));

    /// 교착 시스템 프롬프트. {{CATALOG}} = 등급 A 표(현재 값 제외, 14 §3.4)
    public static string DeadlockSystem => s_deadlock.Value;

    public static string InfraSystem => s_infra.Value;

    private static string Load(string fileName)
    {
        var assembly = typeof(PromptTemplates).Assembly;
        var resource = assembly.GetManifestResourceNames()
            .SingleOrDefault(n => n.EndsWith(".Prompts." + fileName, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"프롬프트 리소스 {fileName}이(가) 어셈블리에 없다");

        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n');
    }
}
