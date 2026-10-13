using FluentAssertions;
using RobotFleet.Shared.Domain;
using RobotFleet.Shared.Messaging;
using RobotFleet.Simulator.Events;
using Xunit;

namespace RobotFleet.Simulator.Tests;

public class EventCollectorDeadlockTests
{
    private static DeadlockDetectedPayload Dead() =>
        new(["R-01", "R-02"], [new CellDto(3, 4), new CellDto(4, 4)], "yield", "R-02", 5, 4, 3, 0, 0);

    private static PathReplannedPayload Replanned(string robot) => new(robot, "T-1", "blocked", 8, 10, 0.5);

    private static ConflictDetectedPayload Conflict() =>
        new("head_on", 3, 4, ["R-01", "R-02"], "R-01", "wait", 100);

    [Fact]
    public void DeadlockAndReplan_AreRank0_BeforeStatusAndTaskPerRobot()
    {
        var c = new EventCollector("m", "r1", 100, TimeProvider.System);
        c.Begin(1, DateTimeOffset.UnixEpoch);
        c.TaskEvent("R-01", EventType.TaskFailed, new TaskFailedPayload("T-1", "R-01", "replan_exceeded", 0, 0));
        c.Status("R-01", RobotStatus.Moving, RobotStatus.Waiting, "conflict_wait");
        c.Replan("R-01", Replanned("R-01"));
        c.Deadlock("R-01", Dead());
        c.Conflict("R-01", Conflict());
        c.Replan("R-00", Replanned("R-00"));

        var batch = c.Complete([]).Single();

        batch.Events.Select(e => e.EventType).Should().Equal(
            EventType.PathReplanned,        // R-00 먼저(robotId 오름차순)
            EventType.PathReplanned,        // R-01 rank 0은 호출 순서대로
            EventType.DeadlockDetected,
            EventType.ConflictDetected,
            EventType.RobotStatusChanged,
            EventType.TaskFailed);
    }

    [Fact]
    public void DeadlockDetected_RoundTripsSearchedDistance_AndIsConflictCategory()
    {
        var c = new EventCollector("m", "r1", 100, TimeProvider.System);
        c.Begin(1, DateTimeOffset.UnixEpoch);
        c.Deadlock("R-02", Dead());

        var item = c.Complete([]).Single().Events.Single();

        item.EventType.Should().Be(EventType.DeadlockDetected);
        item.Payload.GetProperty("searchedDistance").GetInt32().Should().Be(3);
        EventCategories.Of(item.EventType).Should().Be(EventCategory.Conflict);
    }
}
