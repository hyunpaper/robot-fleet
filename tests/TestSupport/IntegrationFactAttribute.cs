using Xunit;

namespace RobotFleet.TestSupport;

/// 지정한 설정 이름 중 하나라도 TestEnv.Get이 null이면 skip
public sealed class IntegrationFactAttribute : FactAttribute
{
    public IntegrationFactAttribute(params string[] settingNames)
    {
        Skip = IntegrationSkip.Reason(settingNames);
    }
}

public sealed class IntegrationTheoryAttribute : TheoryAttribute
{
    public IntegrationTheoryAttribute(params string[] settingNames)
    {
        Skip = IntegrationSkip.Reason(settingNames);
    }
}

internal static class IntegrationSkip
{
    public static string? Reason(IEnumerable<string> settingNames)
    {
        foreach (var name in settingNames)
        {
            if (TestEnv.Get(name) is null)
            {
                return $"{name} 없음 (환경 변수 또는 tests/appsettings.Test.Local.json)";
            }
        }

        return null;
    }
}
