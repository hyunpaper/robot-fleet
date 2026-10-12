using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RobotFleet.Hosting;
using RobotFleet.Infrastructure.Redis;
using RobotFleet.Shared.Pipeline;

namespace RobotFleet.Hosts.Tests;

public sealed class CompositionTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    [Fact]
    public void H02_Ingest_EmptyRabbitMq_Throws()
    {
        var act = () => new ServiceCollection().AddFleetIngest(Config(("ConnectionStrings:RabbitMq", "")));

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void H02_Ingest_EmptyRedis_UsesBypassIdemStore()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddFleetIngest(Config(("ConnectionStrings:RabbitMq", "amqp://localhost")));

        using var sp = services.BuildServiceProvider();
        sp.GetRequiredService<IIdemStore>().Should().BeOfType<BypassIdemStore>();
    }

    [Fact]
    public async Task H03_Worker_StartupStepsRunInRegistrationOrder()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddFleetWorker(Config(
            ("ConnectionStrings:Postgres", "Host=localhost;Database=x"),
            ("ConnectionStrings:RabbitMq", "amqp://localhost")));

        await using var sp = services.BuildServiceProvider();
        sp.GetServices<IStartupStep>().Select(s => s.Name)
            .Should().Equal(ReadinessComponents.PostgresMigrations, ReadinessComponents.RabbitTopology);
    }
}
