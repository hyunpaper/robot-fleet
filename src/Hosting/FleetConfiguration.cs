using Microsoft.Extensions.Configuration;

namespace RobotFleet.Hosting;

public static class FleetConfiguration
{
    /// 순서: json < {Env}.json < Local.json < 환경 변수 < 명령행 (10 §9)
    public static IConfigurationBuilder AddFleetSources(this IConfigurationBuilder builder, string basePath, string[] args)
    {
        builder.AddJsonFile(Path.Combine(basePath, "appsettings.Local.json"), optional: true, reloadOnChange: false);
        builder.AddEnvironmentVariables();
        builder.AddCommandLine(args);
        return builder;
    }

    /// 빈 문자열 · 공백은 null
    public static string? GetConnection(this IConfiguration config, string name)
    {
        var value = config.GetConnectionString(name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
