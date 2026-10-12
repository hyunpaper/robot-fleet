using FluentAssertions;
using RobotFleet.Hosting;
using RobotFleet.Shared.Pipeline;

namespace RobotFleet.Hosts.Tests;

public sealed class HealthTests
{
    [Fact]
    public void H04_AnyComponentNotReady_Returns503WithSnapshot()
    {
        var state = new ReadinessState();
        state.Require(ReadinessComponents.RabbitTopology);
        state.Require(ReadinessComponents.PostgresMigrations);
        state.Set(ReadinessComponents.RabbitTopology, true);

        var (status, body) = HealthEndpoints.Ready(state);

        status.Should().Be(503);
        body[ReadinessComponents.RabbitTopology].Should().BeTrue();
        body[ReadinessComponents.PostgresMigrations].Should().BeFalse();
    }

    [Fact]
    public void H04_AllReady_Returns200()
    {
        var state = new ReadinessState();
        state.Require(ReadinessComponents.RabbitTopology);
        state.Set(ReadinessComponents.RabbitTopology, true);

        HealthEndpoints.Ready(state).StatusCode.Should().Be(200);
    }
}
