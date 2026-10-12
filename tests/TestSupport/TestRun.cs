using System.Globalization;
using System.Text.RegularExpressions;

namespace RobotFleet.TestSupport;

public sealed partial class TestRun
{
    private const string TimeFormat = "yyyyMMddHHmmss";

    private TestRun(string runId) => RunId = runId;

    /// "t" + yyyyMMddHHmmss(UTC) + 소문자 hex 4자리
    public string RunId { get; }

    public string RabbitPrefix => RunId + ".";

    public string RedisPrefix => RunId + ":";

    public string PgSchema => "t_" + RunId;

    public static TestRun New(TimeProvider? time = null)
    {
        var now = (time ?? TimeProvider.System).GetUtcNow().ToUniversalTime();
        var suffix = Random.Shared.Next(0x10000).ToString("x4", CultureInfo.InvariantCulture);
        return new TestRun("t" + now.ToString(TimeFormat, CultureInfo.InvariantCulture) + suffix);
    }

    /// 접두사가 붙은 이름에서도 runId를 찾아 생성 시각을 읽는다
    public static bool TryParseCreatedAt(string resourceName, out DateTimeOffset createdAt)
    {
        createdAt = default;
        var match = RunIdPattern().Match(resourceName);
        return match.Success && DateTimeOffset.TryParseExact(match.Groups[1].Value, TimeFormat,
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out createdAt);
    }

    /// 생성 후 age(기본 1시간)를 넘긴 자원. 시각을 못 읽으면 false
    public static bool IsStale(string resourceName, DateTimeOffset now, TimeSpan? age = null) =>
        TryParseCreatedAt(resourceName, out var createdAt) && now - createdAt > (age ?? TimeSpan.FromHours(1));

    [GeneratedRegex("(?<![A-Za-z0-9])t(\\d{14})[0-9a-f]{4}(?![A-Za-z0-9])")]
    private static partial Regex RunIdPattern();
}
