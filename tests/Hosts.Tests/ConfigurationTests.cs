using FluentAssertions;
using Microsoft.Extensions.Configuration;
using RobotFleet.Hosting;

namespace RobotFleet.Hosts.Tests;

// 환경 변수를 건드리므로 병렬 실행하지 않는다
[Collection("env")]
public sealed class ConfigurationTests : IDisposable
{
    private const string EnvName = "ConnectionStrings__Postgres";
    private readonly string _dir = Directory.CreateTempSubdirectory("hosts-cfg-").FullName;

    public ConfigurationTests() => Environment.SetEnvironmentVariable(EnvName, null);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(EnvName, null);
        Directory.Delete(_dir, recursive: true);
    }

    private IConfigurationRoot Build(params string[] args)
    {
        var builder = new ConfigurationBuilder();
        builder.AddJsonFile(Path.Combine(_dir, "appsettings.json"), optional: false);
        return builder.AddFleetSources(_dir, args).Build();
    }

    private void WriteBase() => File.WriteAllText(Path.Combine(_dir, "appsettings.json"),
        """{ "ConnectionStrings": { "RabbitMq": "", "Redis": "base-redis", "Postgres": "" } }""");

    [Fact]
    public void H01_LocalFileMissing_NoError_AndEmptyIsNull()
    {
        WriteBase();
        var config = Build();

        config.GetConnection("RabbitMq").Should().BeNull();
        config.GetConnection("Redis").Should().Be("base-redis");
    }

    [Fact]
    public void H01_Local_OverridesBase_EnvOverridesLocal_CommandLineOverridesEnv()
    {
        WriteBase();
        File.WriteAllText(Path.Combine(_dir, "appsettings.Local.json"),
            """{ "ConnectionStrings": { "Redis": "local-redis", "Postgres": "local-pg" } }""");

        Build().GetConnection("Redis").Should().Be("local-redis");
        Build().GetConnection("Postgres").Should().Be("local-pg");

        Environment.SetEnvironmentVariable(EnvName, "env-pg");
        Build().GetConnection("Postgres").Should().Be("env-pg");
        Build("--ConnectionStrings:Postgres=cli-pg").GetConnection("Postgres").Should().Be("cli-pg");
    }

    [Fact]
    public void H01_GetConnection_Whitespace_IsNull()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Redis"] = "  " })
            .Build();

        config.GetConnection("Redis").Should().BeNull();
    }
}
