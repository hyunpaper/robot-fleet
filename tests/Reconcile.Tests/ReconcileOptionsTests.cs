using FluentAssertions;

namespace RobotFleet.Reconcile.Tests;

// K-02
public sealed class ReconcileOptionsTests
{
    [Fact]
    public void 기본값은_out_없음_drain_10()
    {
        var options = ReconcileOptions.Parse(["--sendlog", "log.jsonl", "--map", "m1"]);

        options.Should().Be(new ReconcileOptions("log.jsonl", "m1", null, 10));
    }

    [Fact]
    public void 모든_옵션을_읽는다()
    {
        var options = ReconcileOptions.Parse(["--map", "m1", "--sendlog", "d", "--out", "r.json", "--drain-seconds", "3"]);

        options.Should().Be(new ReconcileOptions("d", "m1", "r.json", 3));
    }

    [Theory]
    [InlineData("--map", "m1")]
    [InlineData("--sendlog", "d")]
    public void 필수_누락은_예외(string name, string value)
    {
        var act = () => ReconcileOptions.Parse([name, value]);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void 모르는_옵션은_예외()
    {
        var act = () => ReconcileOptions.Parse(["--sendlog", "d", "--map", "m1", "--nope", "x"]);

        act.Should().Throw<ArgumentException>().WithMessage("*--nope*");
    }

    [Theory]
    [InlineData("--drain-seconds", "abc")]
    [InlineData("--drain-seconds", "-1")]
    public void drain_값이_잘못되면_예외(string name, string value)
    {
        var act = () => ReconcileOptions.Parse(["--sendlog", "d", "--map", "m1", name, value]);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void 값이_없는_옵션은_예외()
    {
        var act = () => ReconcileOptions.Parse(["--sendlog", "d", "--map"]);

        act.Should().Throw<ArgumentException>();
    }
}
