using System.Text;
using FluentAssertions;
using RobotFleet.Shared.Messaging;
using Xunit;

namespace RobotFleet.Shared.Tests.Messaging;

public class DeadlockPayloadJsonTests
{
    private const string YieldJson =
        """{"cycle":["R-012","R-034","R-007"],"cells":[{"x":23,"y":14},{"x":24,"y":14},{"x":24,"y":15}],"resolution":"yield","yieldRobotId":"R-007","yieldToX":24,"yieldToY":16,"searchedDistance":5,"noYieldRetries":0,"resolvedMs":1200}""";

    [Fact] // DL-11
    public void YieldExample_RoundTrips_ToSameBytes_WithSearchedDistance()
    {
        var payload = FleetJson.Deserialize<DeadlockDetectedPayload>(Encoding.UTF8.GetBytes(YieldJson));

        payload.SearchedDistance.Should().Be(5);
        payload.ResolvedMs.Should().Be(1200);
        payload.Resolution.Should().Be("yield");
        payload.YieldRobotId.Should().Be("R-007");
        Encoding.UTF8.GetString(FleetJson.Serialize(payload)).Should().Be(YieldJson);
    }

    [Fact] // DL-11
    public void Payload_Serializes_CamelCase_SearchedDistanceBeforeResolvedMs()
    {
        var payload = new DeadlockDetectedPayload(["R-012", "R-034", "R-007"],
            [new CellDto(23, 14), new CellDto(24, 14), new CellDto(24, 15)], "yield", "R-007", 24, 16, 5, 0, 1200);

        var json = Encoding.UTF8.GetString(FleetJson.Serialize(payload));

        json.Should().Be(YieldJson);
        json.IndexOf("searchedDistance", StringComparison.Ordinal)
            .Should().BeLessThan(json.IndexOf("resolvedMs", StringComparison.Ordinal));
    }

    [Fact] // DL-11
    public void FailedResolution_RoundTrips_WithSearchedDistance()
    {
        var payload = new DeadlockDetectedPayload(["R-001", "R-002"], [new CellDto(5, 5), new CellDto(6, 5)],
            "failed", "", 0, 0, 8, 2, 0);

        var back = FleetJson.Deserialize<DeadlockDetectedPayload>(FleetJson.Serialize(payload));

        back.Should().BeEquivalentTo(payload);
        back.Resolution.Should().Be("failed");
        back.SearchedDistance.Should().Be(8);
    }

    [Fact] // DL-11
    public void FailedJson_WithoutYieldFields_Deserializes_AsNoYield()
    {
        const string json =
            """{"cycle":["R-001","R-002"],"cells":[{"x":5,"y":5}],"resolution":"failed","searchedDistance":8,"resolvedMs":0}""";

        var back = FleetJson.Deserialize<DeadlockDetectedPayload>(Encoding.UTF8.GetBytes(json));

        back.Resolution.Should().Be("failed");
        string.IsNullOrEmpty(back.YieldRobotId).Should().BeTrue();
        back.SearchedDistance.Should().Be(8);
    }

    [Fact] // DL-11
    public void DeadlockPayload_ThroughEnvelopeElement_KeepsSearchedDistance()
    {
        var payload = FleetJson.Deserialize<DeadlockDetectedPayload>(Encoding.UTF8.GetBytes(YieldJson));

        var element = FleetJson.ToElement(payload);

        element.GetProperty("searchedDistance").GetInt32().Should().Be(5);
        FleetJson.Deserialize<DeadlockDetectedPayload>(Encoding.UTF8.GetBytes(element.GetRawText()))
            .Should().BeEquivalentTo(payload);
    }
}
