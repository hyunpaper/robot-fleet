using FluentAssertions;
using RobotFleet.Shared.Geometry;
using RobotFleet.Shared.Map;
using Xunit;

namespace RobotFleet.Simulator.Tests;

public class SimulationOptionsTests
{
    private static GridMap Load(string path) =>
        GridMap.FromDefinition(new MapDefinition("t", 1, 3, 1, 1.0, new[] { "142" },
            new[] { new StationDef("ST-01", 2, 0, "pickup", "EQ-01") }, Array.Empty<ChargerDef>(),
            new[] { new ParkingDef("PK-01", 1, 0) }));

    [Fact]
    public void Parse_Defaults_UseFirstParkingAndStation()
    {
        var (o, _) = SimulationOptions.Parse(new[] { "--map", "m.json" }, Load);

        o.Start.Should().Be(new Point(1, 0));
        o.Goal.Should().Be(new Point(2, 0));
        o.StartHeading.Should().Be(Heading.North);
        o.TickMs.Should().Be(100);
        o.SpeedFactor.Should().Be(1.0);
        o.RenderEveryTicks.Should().Be(5);
    }

    [Fact]
    public void Parse_AllOptions()
    {
        var (o, _) = SimulationOptions.Parse(
            new[] { "--map", "m.json", "--start", "0,0", "--goal", "2,0", "--heading", "e", "--speed", "0", "--tick", "150", "--render", "0", "--max-ticks", "9" },
            Load);

        o.Start.Should().Be(new Point(0, 0));
        o.Goal.Should().Be(new Point(2, 0));
        o.StartHeading.Should().Be(Heading.East);
        o.SpeedFactor.Should().Be(0);
        o.TickMs.Should().Be(150);
        o.RenderEveryTicks.Should().Be(0);
        o.MaxTicks.Should().Be(9);
    }

    [Theory]
    [InlineData("--start", "1")]
    [InlineData("--heading", "X")]
    [InlineData("--tick", "0")]
    [InlineData("--bogus", "1")]
    public void Parse_InvalidArgument_Throws(string key, string value)
    {
        var act = () => SimulationOptions.Parse(new[] { "--map", "m.json", key, value }, Load);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Parse_MissingMap_Throws()
    {
        var act = () => SimulationOptions.Parse(Array.Empty<string>(), Load);
        act.Should().Throw<ArgumentException>();
    }
}
