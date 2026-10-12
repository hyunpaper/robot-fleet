using FluentAssertions;
using RobotFleet.Shared.Messaging;
using Xunit;

namespace RobotFleet.Shared.Tests.Messaging;

public class TopologySpecTests
{
    [Fact] // M-04
    public void Build_HasExpectedCounts()
    {
        var spec = TopologySpec.Build("");

        spec.Exchanges.Should().HaveCount(4);
        spec.Queues.Should().HaveCount(50);
        spec.Bindings.Should().HaveCount(49);   // 파티션 48 + fleet.dlx → fleet.dlq
        spec.Exchanges.Select(e => e.Name).Should().BeEquivalentTo(
            ["fleet.telemetry", "fleet.task", "fleet.conflict", "fleet.dlx"]);
        spec.Exchanges.Single(e => e.Name == "fleet.dlx").Type.Should().Be("fanout");
        spec.Exchanges.Where(e => e.Name != "fleet.dlx").Should().OnlyContain(e => e.Type == "x-consistent-hash");
        spec.Bindings.Where(b => b.Exchange != "fleet.dlx").Should().OnlyContain(b => b.RoutingKey == "1");
        spec.Bindings.Should().Contain(new BindingSpec("fleet.dlx", "fleet.dlq", ""));
    }

    [Theory] // M-04
    [InlineData(EventCategory.Task)]
    [InlineData(EventCategory.Conflict)]
    public void Build_ReliableQueues_HaveQuorumArguments(EventCategory category)
    {
        var spec = TopologySpec.Build("");
        var queues = spec.Queues.Where(q => q.Category == category).ToList();

        queues.Should().HaveCount(16);
        queues.Select(q => q.Partition).Should().Equal(Enumerable.Range(0, 16).Select(i => (int?)i));
        foreach (var queue in queues)
        {
            queue.Arguments.Should().BeEquivalentTo(new Dictionary<string, object>
            {
                ["x-queue-type"] = "quorum",
                ["x-single-active-consumer"] = true,
                ["x-delivery-limit"] = 20L,
                ["x-dead-letter-exchange"] = "fleet.dlx",
                ["x-dead-letter-strategy"] = "at-least-once",
                ["x-overflow"] = "reject-publish",
                ["x-max-length-bytes"] = 67108864L,
            });
        }
    }

    [Fact] // M-04
    public void Build_TelemetryQueues_AreClassicDropHead()
    {
        var queues = TopologySpec.Build("").Queues.Where(q => q.Category == EventCategory.Telemetry).ToList();

        queues.Should().HaveCount(16);
        queues.Should().OnlyContain(q => q.Arguments.Count == 4);
        foreach (var queue in queues)
        {
            queue.Arguments.Should().BeEquivalentTo(new Dictionary<string, object>
            {
                ["x-queue-type"] = "classic",
                ["x-single-active-consumer"] = true,
                ["x-overflow"] = "drop-head",
                ["x-max-length"] = 20000L,
            });
        }
    }

    [Fact] // M-04
    public void Build_DlqAndInvalid_AreQuorumOnly()
    {
        var spec = TopologySpec.Build("");

        foreach (var name in new[] { "fleet.dlq", "fleet.invalid" })
        {
            var queue = spec.Queues.Single(q => q.Name == name);
            queue.Category.Should().BeNull();
            queue.Partition.Should().BeNull();
            queue.Arguments.Should().BeEquivalentTo(new Dictionary<string, object> { ["x-queue-type"] = "quorum" });
        }
    }

    [Fact] // M-04
    public void Build_BindsEachExchange_ToItsOwnSixteenQueues()
    {
        var spec = TopologySpec.Build("");

        spec.Bindings.Should().Contain(new BindingSpec("fleet.task", "fleet.task.p03", "1"));
        spec.Bindings.Should().Contain(new BindingSpec("fleet.telemetry", "fleet.telemetry.p15", "1"));
        spec.Bindings.Where(b => b.Exchange == "fleet.conflict")
            .Should().OnlyContain(b => b.Queue.StartsWith("fleet.conflict.p"));
        spec.Bindings.Where(b => b.Exchange != "fleet.dlx").GroupBy(b => b.Exchange).Should().OnlyContain(g => g.Count() == 16);
    }

    [Fact] // M-04
    public void Build_WithPrefix_PrefixesEveryName()
    {
        var spec = TopologySpec.Build("t1.");

        spec.Exchanges.Should().OnlyContain(e => e.Name.StartsWith("t1."));
        spec.Queues.Should().OnlyContain(q => q.Name.StartsWith("t1."));
        spec.Bindings.Should().OnlyContain(b => b.Exchange.StartsWith("t1.") && b.Queue.StartsWith("t1."));
        spec.Queues.Where(q => q.Category is EventCategory.Task or EventCategory.Conflict)
            .Should().OnlyContain(q => (string)q.Arguments["x-dead-letter-exchange"] == "t1.fleet.dlx");
        TopologySpec.QueueName(EventCategory.Task, 3, "t1.").Should().Be("t1.fleet.task.p03");
        TopologySpec.ExchangeName(EventCategory.Task, "t1.").Should().Be("t1.fleet.task");
        TopologySpec.DlqName("t1.").Should().Be("t1.fleet.dlq");
        TopologySpec.InvalidQueueName("t1.").Should().Be("t1.fleet.invalid");
    }
}
