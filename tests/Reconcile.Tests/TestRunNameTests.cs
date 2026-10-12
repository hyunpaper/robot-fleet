using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using RobotFleet.TestSupport;

namespace RobotFleet.Reconcile.Tests;

// K-03
public sealed class TestRunNameTests
{
    [Fact]
    public void RunId_형식()
    {
        var run = TestRun.New();

        run.RunId.Should().MatchRegex(@"^t\d{14}[0-9a-f]{4}$");
        run.RabbitPrefix.Should().Be(run.RunId + ".");
        run.RedisPrefix.Should().Be(run.RunId + ":");
        run.PgSchema.Should().Be("t_" + run.RunId);
    }

    [Fact]
    public void 주입한_시계의_UTC로_만든다()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 12, 9, 30, 0, TimeSpan.FromHours(9)));

        TestRun.New(time).RunId.Should().StartWith("t20261012003000");
    }

    [Theory]
    [InlineData("t20261012093000ab12.fleet.task.p00")]
    [InlineData("t_t20261012093000ab12")]
    [InlineData("t20261012093000ab12:idem:x")]
    [InlineData("t20261012093000ab12")]
    public void 접두사가_붙은_이름에서_시각을_읽는다(string name)
    {
        TestRun.TryParseCreatedAt(name, out var createdAt).Should().BeTrue();

        createdAt.Should().Be(new DateTimeOffset(2026, 10, 12, 9, 30, 0, TimeSpan.Zero));
    }

    [Theory]
    [InlineData("fleet.task.p00")]
    [InlineData("t2026101209300ab12")]
    [InlineData("t20261013999999ab12")]
    public void 실행_이름이_아니면_파싱_실패(string name)
    {
        TestRun.TryParseCreatedAt(name, out _).Should().BeFalse();
        TestRun.IsStale(name, DateTimeOffset.MaxValue, TimeSpan.Zero).Should().BeFalse();
    }

    [Fact]
    public void IsStale_경계()
    {
        const string name = "t20261012093000ab12.fleet.task.p00";
        var created = new DateTimeOffset(2026, 10, 12, 9, 30, 0, TimeSpan.Zero);
        var age = TimeSpan.FromHours(1);

        TestRun.IsStale(name, created + age - TimeSpan.FromSeconds(1), age).Should().BeFalse();
        TestRun.IsStale(name, created + age, age).Should().BeFalse();
        TestRun.IsStale(name, created + age + TimeSpan.FromSeconds(1), age).Should().BeTrue();
        TestRun.IsStale(name, created + TimeSpan.FromMinutes(61)).Should().BeTrue();
        TestRun.IsStale(name, created + TimeSpan.FromMinutes(59)).Should().BeFalse();
    }
}
