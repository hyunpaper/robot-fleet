using FluentAssertions;
using Xunit;
using RobotFleet.Shared.Pipeline;

namespace RobotFleet.Shared.Tests.Pipeline;

public sealed class BatchDedupTests
{
    private static Delivery D(ulong tag, string messageId) =>
        new("fleet.task.p00", tag, messageId, "TaskCreated", 1, ReadOnlyMemory<byte>.Empty, false, 1);

    [Fact]
    public void 중복_없으면_그대로_둔다()
    {
        var batch = new[] { D(1, "b/0"), D(2, "b/1"), D(3, "b/2") };

        var result = BatchDedup.ByEventId(batch);

        result.Duplicates.Should().Be(0);
        result.Unique.Should().Equal(batch);
    }

    [Fact]
    public void 같은_eventId는_처음_1건만_순서대로_남긴다()
    {
        var first = D(1, "b/0");
        var second = D(2, "b/1");
        var dup = D(3, "b/0");   // 재송신된 사본

        var result = BatchDedup.ByEventId(new[] { first, second, dup });

        result.Duplicates.Should().Be(1);
        result.Unique.Should().Equal(first, second);   // tag 1 · 2만, 순서 유지
    }

    [Fact]
    public void 같은_eventId가_셋이면_둘을_센다()
    {
        var result = BatchDedup.ByEventId(new[] { D(1, "b/0"), D(2, "b/0"), D(3, "b/0") });

        result.Duplicates.Should().Be(2);
        result.Unique.Should().ContainSingle().Which.DeliveryTag.Should().Be(1UL);
    }

    [Fact]
    public void 빈_MessageId는_중복으로_묶지_않는다()
    {
        // 빈 id끼리는 서로 다른 독 메시지일 수 있다 — 전부 남겨 역직렬화 단계에서 개별 판단
        var result = BatchDedup.ByEventId(new[] { D(1, ""), D(2, ""), D(3, "b/0") });

        result.Duplicates.Should().Be(0);
        result.Unique.Should().HaveCount(3);
    }

    [Fact]
    public void 빈_배치는_빈_결과()
    {
        var result = BatchDedup.ByEventId(Array.Empty<Delivery>());

        result.Duplicates.Should().Be(0);
        result.Unique.Should().BeEmpty();
    }
}
