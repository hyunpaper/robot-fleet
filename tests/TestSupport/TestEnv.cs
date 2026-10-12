using Microsoft.Extensions.Configuration;

namespace RobotFleet.TestSupport;

public static class TestEnv
{
    public const string RabbitMqVar = "FLEET_TEST_RABBITMQ";
    public const string RedisVar = "FLEET_TEST_REDIS";
    public const string PgVar = "FLEET_TEST_PG";
    public const string RabbitMgmtVar = "FLEET_TEST_RABBITMQ_MGMT";

    private static readonly Lazy<IConfiguration?> Files = new(() => LoadFiles(TestsDirectory));

    public static string? RabbitMq => Get(RabbitMqVar);

    public static string? Redis => Get(RedisVar);

    public static string? Pg => Get(PgVar);

    public static string? RabbitMgmt => Get(RabbitMgmtVar);

    /// RobotFleet.sln이 있는 폴더 아래 tests/. 못 찾으면 null
    public static string? TestsDirectory { get; } = FindTestsDirectory();

    /// 환경 변수 > appsettings.Test.Local.json > appsettings.Test.json. 빈 값은 null
    public static string? Get(string name) =>
        Pick(Environment.GetEnvironmentVariable(name)) ?? Pick(Files.Value?[name]);

    internal static string? Resolve(string name, Func<string, string?> environment, string? testsDirectory) =>
        Pick(environment(name)) ?? Pick(LoadFiles(testsDirectory)?[name]);

    private static string? Pick(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    // 뒤에 추가한 파일이 이긴다: Test.json → Test.Local.json
    private static IConfiguration? LoadFiles(string? testsDirectory)
    {
        if (testsDirectory is null)
        {
            return null;
        }

        var builder = new ConfigurationBuilder();
        var any = false;
        foreach (var file in new[] { "appsettings.Test.json", "appsettings.Test.Local.json" })
        {
            var path = Path.Combine(testsDirectory, file);
            if (File.Exists(path))
            {
                builder.AddJsonFile(path, optional: true, reloadOnChange: false);
                any = true;
            }
        }

        return any ? builder.Build() : null;
    }

    private static string? FindTestsDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "RobotFleet.sln")))
            {
                return Path.Combine(dir.FullName, "tests");
            }
        }

        return null;
    }
}
