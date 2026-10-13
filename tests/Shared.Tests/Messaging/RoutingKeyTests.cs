using System.Text.Json;
using FluentAssertions;
using RobotFleet.Shared.Messaging;
using Xunit;

namespace RobotFleet.Shared.Tests.Messaging;

public class RoutingKeyTests
{
    [Theory] // M-02
    [InlineData("RobotTelemetry", """{"robotId":"R-001","x":1}""", "R-001")]
    [InlineData("RobotStatusChanged", """{"robotId":"R-002","from":1,"to":2,"reason":"conflict_wait"}""", "R-002")]
    [InlineData("TaskCreated", """{"taskId":"T-1"}""", "T-1")]
    [InlineData("TaskAssigned", """{"taskId":"T-2","robotId":"R-9"}""", "T-2")]
    [InlineData("TaskStarted", """{"taskId":"T-3","robotId":"R-9"}""", "T-3")]
    [InlineData("TaskCompleted", """{"taskId":"T-4","robotId":"R-9"}""", "T-4")]
    [InlineData("TaskFailed", """{"taskId":"T-5","reason":"no_path"}""", "T-5")]
    [InlineData("ConflictDetected", """{"robotIds":["R-1","R-2"]}""", "factory-a")]
    [InlineData("DeadlockDetected", """{"cycle":["R-1","R-2"]}""", "factory-a")]
    [InlineData("PathReplanned", """{"robotId":"R-1"}""", "factory-a")]
    public void Of_ReturnsExpectedKey(string eventType, string payloadJson, string expected)
    {
        using var doc = JsonDocument.Parse(payloadJson);

        RoutingKeys.Of(eventType, doc.RootElement, "factory-a").Should().Be(expected);
    }

    [Fact] // M-02
    public void Of_UnknownEventType_Throws()
    {
        using var doc = JsonDocument.Parse("{}");

        var act = () => RoutingKeys.Of("Nope", doc.RootElement, "m");

        act.Should().Throw<ArgumentException>();
    }

    [Fact] // M-02
    public void Of_MissingKeyField_Throws()
    {
        using var doc = JsonDocument.Parse("""{"x":1}""");

        var act = () => RoutingKeys.Of("RobotTelemetry", doc.RootElement, "m");

        act.Should().Throw<ArgumentException>();
    }

    [Fact] // M-02
    public void EveryEventType_HasRoutingKeyAndCategory()
    {
        using var doc = JsonDocument.Parse("""{"robotId":"R","taskId":"T"}""");

        foreach (var type in EventType.All)
        {
            var act = () => RoutingKeys.Of(type, doc.RootElement, "m");
            act.Should().NotThrow(type);
            var category = EventCategories.Of(type);
            var key = RoutingKeys.Of(type, doc.RootElement, "m");
            var byMapId = category == EventCategory.Conflict || type is EventType.SimulationStarted or EventType.SimulationEnded;
            (byMapId ? key == "m" : key is "R" or "T").Should().BeTrue(type);
        }
    }
}
