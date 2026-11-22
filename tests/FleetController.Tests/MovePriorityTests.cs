using FluentAssertions;
using RobotFleet.FleetController.Arbitration;
using RobotFleet.Shared.Geometry;
using Xunit;

namespace RobotFleet.FleetController.Tests;

/// 14 §2.1: 가중치를 TuningDefaults 상수로 옮긴 뒤에도 점수가 비트 단위로 같아야 한다(13 §9 결정성)
public class MovePriorityTests
{
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(1500, 37, 2)]
    [InlineData(123_457, 250, 1)]
    [InlineData(10, 100, 0)]
    public void Score_EqualsLegacyLiteralFormula(int waitMs, int remainingSteps, int taskPriority)
    {
        var r = new MoveRequest("R1", new Point(0, 0), new Point(1, 0), Heading.East, 0, waitMs, remainingSteps, taskPriority);
        double legacy = r.WaitMs * 0.01 + (100 - r.RemainingSteps) + r.TaskPriority * 50;   // 카탈로그 이전 리터럴

        MovePriority.Score(r).Should().Be(legacy);
    }
}
